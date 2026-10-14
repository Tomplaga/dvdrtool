using CdExtraStudio.Models;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

public sealed class CdAudioService : IDisposable
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_CDROM_READ_TOC = 0x00024000;
    private const uint IOCTL_CDROM_RAW_READ = 0x0002403E;
    private const int RAW_SECTOR_SIZE = 2352;
    private const int SECTORS_PER_READ = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawReadInfo
    {
        public long DiskOffset;
        public uint SectorCount;
        public TrackModeType TrackMode;
    }

    private enum TrackModeType : int
    {
        YellowMode2 = 0,
        XAForm2 = 1,
        CDDA = 2,
        RawWithC2AndSubCode = 3,
        RawWithC2 = 4,
        RawWithSubCode = 5
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr winHandle);

    private string? _openDrive;

    public IReadOnlyList<CdTrack> ReadTracks(string driveRoot)
    {
        var driveLetter = NormalizeDrive(driveRoot);
        using var handle = OpenDevice(driveLetter);
        var toc = new byte[804];
        if (!DeviceIoControl(handle, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, toc, (uint)toc.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo leer la tabla de contenidos del CD.");

        int firstTrack = toc[2];
        int lastTrack = toc[3];
        if (firstTrack == 0 || lastTrack < firstTrack) return Array.Empty<CdTrack>();

        var starts = new Dictionary<int, (int lba, bool audio)>();
        int entries = Math.Min(100, lastTrack - firstTrack + 2); // incluye lead-out
        for (int i = 0; i < entries; i++)
        {
            int o = 4 + i * 8;
            byte controlAdr = toc[o + 1];
            int trackNumber = toc[o + 2];
            int minute = toc[o + 5];
            int second = toc[o + 6];
            int frame = toc[o + 7];
            int lba = (minute * 60 + second) * 75 + frame - 150;
            int control = controlAdr & 0x0F;
            bool audio = (control & 0x04) == 0;
            starts[trackNumber] = (lba, audio);
        }

        if (!starts.TryGetValue(0xAA, out var leadOut))
        {
            int o = 4 + (lastTrack - firstTrack + 1) * 8;
            int minute = toc[o + 5], second = toc[o + 6], frame = toc[o + 7];
            leadOut = ((minute * 60 + second) * 75 + frame - 150, false);
        }

        var list = new List<CdTrack>();
        for (int t = firstTrack; t <= lastTrack; t++)
        {
            if (!starts.TryGetValue(t, out var current)) continue;
            int end = t == lastTrack ? leadOut.lba : (starts.TryGetValue(t + 1, out var next) ? next.lba : leadOut.lba);
            list.Add(new CdTrack { Number = t, StartLba = current.lba, EndLba = end, IsAudio = current.audio });
        }
        return list;
    }

    public void PlayTrack(string driveRoot, int trackNumber)
    {
        var drive = NormalizeDrive(driveRoot);
        Stop();
        CheckMci($"open {drive} type cdaudio alias cdextra wait");
        _openDrive = drive;
        CheckMci("set cdextra time format tmsf wait");
        CheckMci($"play cdextra from {trackNumber}:0:0:0 to {trackNumber + 1}:0:0:0");
    }

    public void Stop()
    {
        if (_openDrive is null) return;
        mciSendString("stop cdextra wait", null, 0, IntPtr.Zero);
        mciSendString("close cdextra wait", null, 0, IntPtr.Zero);
        _openDrive = null;
    }

    public async Task RipTrackAsync(string driveRoot, CdTrack track, string outputWav,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!track.IsAudio) throw new InvalidOperationException("La pista seleccionada no es de audio.");
        var drive = NormalizeDrive(driveRoot);
        using var handle = OpenDevice(drive);
        Directory.CreateDirectory(Path.GetDirectoryName(outputWav)!);

        await using var fs = new FileStream(outputWav, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        WriteWaveHeader(fs, track.SectorCount * RAW_SECTOR_SIZE);

        int current = track.StartLba;
        int remaining = track.SectorCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sectors = Math.Min(SECTORS_PER_READ, remaining);
            var buffer = new byte[sectors * RAW_SECTOR_SIZE];
            var info = new RawReadInfo
            {
                DiskOffset = (long)current * 2048L,
                SectorCount = (uint)sectors,
                TrackMode = TrackModeType.CDDA
            };
            IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<RawReadInfo>());
            try
            {
                Marshal.StructureToPtr(info, pInfo, false);
                if (!DeviceIoControl(handle, IOCTL_CDROM_RAW_READ, pInfo, (uint)Marshal.SizeOf<RawReadInfo>(), buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Error leyendo sectores de la pista {track.Number}.");
                await fs.WriteAsync(buffer.AsMemory(0, (int)read), cancellationToken);
            }
            finally
            {
                Marshal.FreeHGlobal(pInfo);
            }
            current += sectors;
            remaining -= sectors;
            progress?.Report(1.0 - remaining / (double)Math.Max(1, track.SectorCount));
        }
    }

    private static SafeFileHandle OpenDevice(string drive)
    {
        var handle = CreateFile($@"\\.\{drive}", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se pudo abrir la unidad {drive}.");
        return handle;
    }

    private static string NormalizeDrive(string driveRoot)
        => driveRoot.Trim().TrimEnd('\\').Substring(0, 2); // D:

    private static void CheckMci(string command)
    {
        int result = mciSendString(command, null, 0, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"MCI devolvió el error {result} al ejecutar: {command}");
    }

    private static void WriteWaveHeader(Stream stream, int pcmBytes)
    {
        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)2);
        bw.Write(44100);
        bw.Write(44100 * 2 * 16 / 8);
        bw.Write((short)(2 * 16 / 8));
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmBytes);
    }

    public void Dispose() => Stop();
}
