using System;
using System.IO;
using System.Text;

namespace CdExtraStudio.Services;

/// <summary>
/// Normaliza WAV PCM/IEEE-float comunes a CD-DA: PCM 16-bit, 44.1 kHz, estéreo.
/// No necesita FFmpeg. Acepta PCM entero de 8/16/24/32/64 bits y float de 32/64 bits,
/// con una o más canales y cualquier frecuencia de muestreo válida.
/// </summary>
public static class PcmWavNormalizer
{
    private const int TargetRate = 44100;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private sealed class WaveInfo
    {
        public ushort FormatTag { get; init; }
        public ushort Channels { get; init; }
        public uint SampleRate { get; init; }
        public ushort BlockAlign { get; init; }
        public ushort BitsPerSample { get; init; }
        public Guid? SubFormat { get; init; }
        public long DataOffset { get; init; }
        public uint DataSize { get; init; }

        public bool IsPcm => FormatTag == 1 || (FormatTag == 0xFFFE && SubFormat == PcmSubFormat);
        public bool IsFloat => FormatTag == 3 || (FormatTag == 0xFFFE && SubFormat == FloatSubFormat);
    }

    public static bool CanNormalize(string wavPath, out string? reason)
    {
        try
        {
            WaveInfo info = ReadWaveInfo(wavPath);
            ValidateSupported(info, wavPath);
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static void ConvertToCdda(string sourceWav, string targetWav)
    {
        if (string.Equals(Path.GetFullPath(sourceWav), Path.GetFullPath(targetWav), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El WAV de origen y destino deben ser archivos distintos.", nameof(targetWav));

        WaveInfo info = ReadWaveInfo(sourceWav);
        ValidateSupported(info, sourceWav);

        long inputFrames = info.DataSize / info.BlockAlign;
        if (inputFrames <= 0)
            throw new InvalidDataException($"{Path.GetFileName(sourceWav)} no contiene muestras de audio.");

        long outputFrames = Math.Max(1L, (long)Math.Round(inputFrames * (double)TargetRate / info.SampleRate));
        long outputDataSize64 = checked(outputFrames * 4L);
        if (outputDataSize64 > uint.MaxValue - 36)
            throw new InvalidDataException("El archivo convertido sería demasiado grande para un WAV RIFF estándar.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetWav))!);

        using var input = File.Open(sourceWav, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
        input.Position = info.DataOffset;

        using var output = File.Create(targetWav);
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        WriteCddaHeader(writer, (uint)outputDataSize64);

        StereoFrame frameA = ReadStereoFrame(reader, info);
        StereoFrame frameB = inputFrames > 1 ? ReadStereoFrame(reader, info) : frameA;
        long baseIndex = 0;
        long nextFrameIndex = inputFrames > 1 ? 1 : 0;
        double ratio = info.SampleRate / (double)TargetRate;

        for (long outIndex = 0; outIndex < outputFrames; outIndex++)
        {
            double sourcePosition = outIndex * ratio;

            while (sourcePosition > baseIndex + 1 && nextFrameIndex < inputFrames - 1)
            {
                frameA = frameB;
                frameB = ReadStereoFrame(reader, info);
                baseIndex++;
                nextFrameIndex++;
            }

            double fraction = Math.Clamp(sourcePosition - baseIndex, 0.0, 1.0);
            double left = frameA.Left + (frameB.Left - frameA.Left) * fraction;
            double right = frameA.Right + (frameB.Right - frameA.Right) * fraction;

            writer.Write(ToInt16(left));
            writer.Write(ToInt16(right));
        }
    }

    private static WaveInfo ReadWaveInfo(string wavPath)
    {
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("No se encontró el archivo WAV.", wavPath);

        using var fs = File.OpenRead(wavPath);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

        if (fs.Length < 12 || new string(br.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} no es un WAV RIFF compatible.");

        br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} no contiene un formato WAVE válido.");

        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort blockAlign = 0;
        ushort bits = 0;
        Guid? subFormat = null;
        long dataOffset = 0;
        uint dataSize = 0;
        bool haveFmt = false;

        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(br.ReadChars(4));
            uint size = br.ReadUInt32();
            long chunkStart = fs.Position;
            long next = chunkStart + size + (size % 2);
            if (chunkStart + size > fs.Length)
                throw new InvalidDataException($"{Path.GetFileName(wavPath)} contiene un bloque WAV incompleto.");

            if (id == "fmt " && size >= 16)
            {
                formatTag = br.ReadUInt16();
                channels = br.ReadUInt16();
                sampleRate = br.ReadUInt32();
                br.ReadUInt32(); // byte rate
                blockAlign = br.ReadUInt16();
                bits = br.ReadUInt16();
                haveFmt = true;

                if (formatTag == 0xFFFE && size >= 40)
                {
                    ushort cbSize = br.ReadUInt16();
                    if (cbSize >= 22)
                    {
                        br.ReadUInt16(); // valid bits
                        br.ReadUInt32(); // channel mask
                        byte[] guidBytes = br.ReadBytes(16);
                        if (guidBytes.Length == 16) subFormat = new Guid(guidBytes);
                    }
                }
            }
            else if (id == "data")
            {
                dataOffset = fs.Position;
                dataSize = size;
            }

            fs.Position = Math.Min(next, fs.Length);
            if (haveFmt && dataOffset > 0) break;
        }

        if (!haveFmt || dataOffset == 0 || dataSize == 0)
            throw new InvalidDataException($"{Path.GetFileName(wavPath)} no contiene los bloques fmt/data necesarios.");

        return new WaveInfo
        {
            FormatTag = formatTag,
            Channels = channels,
            SampleRate = sampleRate,
            BlockAlign = blockAlign,
            BitsPerSample = bits,
            SubFormat = subFormat,
            DataOffset = dataOffset,
            DataSize = dataSize
        };
    }

    private static void ValidateSupported(WaveInfo info, string path)
    {
        if (info.Channels == 0 || info.Channels > 32)
            throw new InvalidDataException($"{Path.GetFileName(path)} tiene una cantidad de canales no compatible ({info.Channels}).");
        if (info.SampleRate < 1000 || info.SampleRate > 768000)
            throw new InvalidDataException($"{Path.GetFileName(path)} tiene una frecuencia de muestreo no compatible ({info.SampleRate} Hz).");
        if (info.BlockAlign == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} tiene un block-align WAV inválido.");

        if (info.IsPcm)
        {
            if (info.BitsPerSample is not (8 or 16 or 24 or 32 or 64))
                throw new InvalidDataException($"PCM de {info.BitsPerSample} bits no está soportado por el conversor interno.");
            return;
        }

        if (info.IsFloat)
        {
            if (info.BitsPerSample is not (32 or 64))
                throw new InvalidDataException($"Audio IEEE-float de {info.BitsPerSample} bits no está soportado por el conversor interno.");
            return;
        }

        throw new InvalidDataException(
            $"{Path.GetFileName(path)} usa un códec WAV comprimido (formato 0x{info.FormatTag:X4}). " +
            "Para ese tipo de WAV todavía se necesita FFmpeg.");
    }

    private readonly struct StereoFrame
    {
        public StereoFrame(double left, double right)
        {
            Left = left;
            Right = right;
        }

        public double Left { get; }
        public double Right { get; }
    }

    private static StereoFrame ReadStereoFrame(BinaryReader reader, WaveInfo info)
    {
        int bytesPerSample = info.BitsPerSample / 8;
        int consumed = 0;
        double first = 0;
        double second = 0;
        double extraSum = 0;
        int extraCount = 0;

        for (int channel = 0; channel < info.Channels; channel++)
        {
            double value = ReadSample(reader, info);
            consumed += bytesPerSample;

            if (channel == 0) first = value;
            else if (channel == 1) second = value;
            else
            {
                extraSum += value;
                extraCount++;
            }
        }

        int padding = info.BlockAlign - consumed;
        if (padding < 0)
            throw new InvalidDataException("El block-align del WAV es menor que el tamaño de sus muestras.");
        if (padding > 0)
            reader.BaseStream.Seek(padding, SeekOrigin.Current);

        if (info.Channels == 1)
            return new StereoFrame(first, first);
        if (info.Channels == 2)
            return new StereoFrame(first, second);

        // Conserva principalmente L/R y mezcla suavemente los canales adicionales.
        // Evita depender de una máscara de canales concreta para WAV multicanal genérico.
        double extras = extraCount > 0 ? extraSum / extraCount : 0;
        return new StereoFrame(
            ClampUnit(first * 0.75 + extras * 0.25),
            ClampUnit(second * 0.75 + extras * 0.25));
    }

    private static double ReadSample(BinaryReader reader, WaveInfo info)
    {
        if (info.IsFloat)
        {
            return info.BitsPerSample switch
            {
                32 => ClampUnit(reader.ReadSingle()),
                64 => ClampUnit(reader.ReadDouble()),
                _ => throw new InvalidDataException("Profundidad float no compatible.")
            };
        }

        return info.BitsPerSample switch
        {
            8 => (reader.ReadByte() - 128) / 128.0,
            16 => reader.ReadInt16() / 32768.0,
            24 => ReadInt24(reader) / 8388608.0,
            32 => reader.ReadInt32() / 2147483648.0,
            64 => reader.ReadInt64() / 9223372036854775808.0,
            _ => throw new InvalidDataException("Profundidad PCM no compatible.")
        };
    }

    private static int ReadInt24(BinaryReader reader)
    {
        int value = reader.ReadByte() | (reader.ReadByte() << 8) | (reader.ReadByte() << 16);
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
        return value;
    }

    private static short ToInt16(double value)
    {
        value = ClampUnit(value);
        if (value >= 1.0) return short.MaxValue;
        if (value <= -1.0) return short.MinValue;
        return (short)Math.Round(value * 32767.0, MidpointRounding.AwayFromZero);
    }

    private static double ClampUnit(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value > 1.0) return 1.0;
        if (value < -1.0) return -1.0;
        return value;
    }

    private static void WriteCddaHeader(BinaryWriter writer, uint dataSize)
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36u + dataSize));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);      // PCM
        writer.Write((ushort)2);      // stereo
        writer.Write((uint)TargetRate);
        writer.Write((uint)(TargetRate * 4));
        writer.Write((ushort)4);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
    }
}
