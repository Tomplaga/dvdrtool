using System;
using System.IO;
using System.Text;

namespace CdExtraStudio.Services;

public static class WavHelper
{
    public static void ValidateCddaWav(string wavPath)
    {
        ReadCddaInfo(wavPath, out _, out _);
    }

    public static bool IsCddaWav(string wavPath, out string? reason)
    {
        try
        {
            ValidateCddaWav(wavPath);
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }


    public static long GetCddaSectorCount(string wavPath)
    {
        ReadCddaInfo(wavPath, out _, out uint dataSize);
        return (dataSize + 2351L) / 2352L;
    }

    public static void GetCddaTrackLength(string wavPath, out long fullSectors, out int remainingSamples)
    {
        ReadCddaInfo(wavPath, out _, out uint dataSize);

        // PCM CD-DA: 16-bit estéreo = 4 bytes por frame; 588 frames por sector.
        long sampleFrames = dataSize / 4L;
        fullSectors = sampleFrames / 588L;
        remainingSamples = (int)(sampleFrames % 588L);
    }

    public static string CreateRawPcmTempFile(string wavPath)
    {
        ReadCddaInfo(wavPath, out long dataPos, out uint dataSize);

        using var fs = File.OpenRead(wavPath);
        string temp = Path.Combine(Path.GetTempPath(), $"cdextra_{Guid.NewGuid():N}.pcm");
        fs.Position = dataPos;
        using var output = File.Create(temp);
        var buffer = new byte[1024 * 1024];
        long remaining = dataSize;
        long written = 0;

        while (remaining > 0)
        {
            int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) break;
            output.Write(buffer, 0, read);
            written += read;
            remaining -= read;
        }

        int padding = (int)((2352 - (written % 2352)) % 2352);
        if (padding > 0) output.Write(new byte[padding]);
        return temp;
    }

    private static void ReadCddaInfo(string wavPath, out long dataPos, out uint dataSize)
    {
        if (!File.Exists(wavPath)) throw new FileNotFoundException("No se encontró el archivo WAV.", wavPath);

        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        if (fs.Length < 12 || new string(br.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} no es WAV RIFF.");

        br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} no contiene un formato WAVE válido.");

        ushort format = 0, channels = 0, bits = 0;
        uint sampleRate = 0;
        dataPos = 0;
        dataSize = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(br.ReadChars(4));
            uint size = br.ReadUInt32();
            long next = fs.Position + size + (size % 2);
            if (next > fs.Length + 1) break;

            if (id == "fmt " && size >= 16)
            {
                format = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt16();
                bits = br.ReadUInt16();
            }
            else if (id == "data")
            {
                dataPos = fs.Position;
                dataSize = size;
                break;
            }

            fs.Position = Math.Min(next, fs.Length);
        }

        if (format != 1 || channels != 2 || sampleRate != 44100 || bits != 16)
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} debe ser PCM estéreo, 44.1 kHz y 16 bits.");
        if (dataPos == 0 || dataSize == 0)
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} no contiene un bloque de audio válido.");
    }
}
