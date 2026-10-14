using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

/// <summary>
/// Obtiene la duración de audio sin convertirlo. Usa lectores internos para
/// formatos comunes, metadatos multimedia de Windows y, si está disponible,
/// ffprobe/FFmpeg como respaldo universal. La comprobación de capacidad nunca
/// debe obligar a decodificar las pistas.
/// </summary>
public static class AudioDurationEstimator
{
    private static readonly Regex FfmpegDurationRegex = new(
        @"Duration:\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:[\.,]\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryGetCddaSectorCount(string path, out long sectors, out string detail)
    {
        sectors = 0;
        detail = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            detail = "archivo no encontrado";
            return false;
        }

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            double seconds = ext switch
            {
                ".wav" => ReadWavDuration(path),
                ".flac" => ReadFlacDuration(path),
                ".opus" or ".ogg" or ".oga" => ReadOggDuration(path),
                ".aif" or ".aiff" or ".aifc" => ReadAiffDuration(path),
                ".mp3" or ".mp2" or ".mp1" => ReadMpegAudioDuration(path),
                ".m4a" or ".m4b" or ".m4p" or ".mp4" or ".mov" or ".3gp" or ".3g2" => ReadIsoBaseMediaDuration(path),
                ".aac" => ReadAdtsAacDuration(path),
                ".wma" or ".asf" => ReadAsfDuration(path),
                ".ape" => ReadApeDuration(path),
                ".wv" => ReadWavPackDuration(path),
                ".amr" or ".awb" => ReadAmrDuration(path),
                ".au" or ".snd" => ReadAuDuration(path),
                _ => 0
            };

            if (TryConvertSeconds(seconds, "lector interno", out sectors, out detail))
                return true;

            // Windows Explorer/Media Foundation puede conocer la duración de
            // muchos contenedores aunque no tengamos un parser propio.
            if (TryReadWindowsShellDuration(path, out seconds)
                && TryConvertSeconds(seconds, "metadatos de Windows", out sectors, out detail))
                return true;

            detail = string.IsNullOrWhiteSpace(ext)
                ? "duración no disponible desde el contenedor ni desde Windows"
                : $"duración no disponible directamente para {ext}";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            sectors = 0;
            return false;
        }
    }

    public static async Task<(bool Success, long Sectors, string Detail)> TryGetCddaSectorCountAsync(
        string path,
        string? ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        if (TryGetCddaSectorCount(path, out long sectors, out string detail))
            return (true, sectors, detail);

        // ffprobe es preferible porque devuelve solo el valor numérico.
        string? ffprobe = FindFfprobe(ffmpegPath);
        if (!string.IsNullOrWhiteSpace(ffprobe))
        {
            try
            {
                double seconds = await ProbeWithFfprobeAsync(ffprobe, path, cancellationToken);
                if (TryConvertSeconds(seconds, "ffprobe", out sectors, out detail))
                    return (true, sectors, detail);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Un prober defectuoso no debe hacer fallar Comprobar. */ }
        }

        string? ffmpeg = NormalizeExecutable(ffmpegPath) ?? AudioConversionService.FindFfmpeg();
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            try
            {
                double seconds = await ProbeWithFfmpegAsync(ffmpeg, path, cancellationToken);
                if (TryConvertSeconds(seconds, "FFmpeg", out sectors, out detail))
                    return (true, sectors, detail);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Igual: se deja la pista como pendiente. */ }
        }

        return (false, 0, string.IsNullOrWhiteSpace(detail) ? "duración no disponible sin decodificar" : detail);
    }

    private static bool TryConvertSeconds(double seconds, string source, out long sectors, out string detail)
    {
        sectors = 0;
        detail = "";
        if (!(seconds > 0) || double.IsNaN(seconds) || double.IsInfinity(seconds)) return false;

        // Un sector CD-DA equivale a 1/75 s. Redondeamos hacia arriba por pista,
        // igual que el padding de cdrecord al final de cada pista.
        sectors = Math.Max(1, (long)Math.Ceiling(seconds * 75.0));
        detail = $"{FormatDuration(seconds)} · {sectors} sectores CD-DA · {source}";
        return true;
    }

    private static double ReadWavDuration(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        if (fs.Length < 12 || new string(br.ReadChars(4)) != "RIFF") return 0;
        br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE") return 0;

        uint byteRate = 0;
        ulong dataSize = 0;
        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(br.ReadChars(4));
            uint size = br.ReadUInt32();
            long payload = fs.Position;
            long next = payload + size + (size & 1);
            if (next < payload || next > fs.Length + 1) break;

            if (id == "fmt " && size >= 16)
            {
                br.ReadUInt16();
                br.ReadUInt16();
                br.ReadUInt32();
                byteRate = br.ReadUInt32();
            }
            else if (id == "data")
            {
                dataSize = size;
                if (byteRate > 0) break;
            }
            fs.Position = Math.Min(next, fs.Length);
        }

        return byteRate > 0 && dataSize > 0 ? dataSize / (double)byteRate : 0;
    }

    private static double ReadFlacDuration(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4 || Encoding.ASCII.GetString(magic) != "fLaC") return 0;

        Span<byte> lenBytes = stackalloc byte[3];
        while (fs.Position + 4 <= fs.Length)
        {
            int header = fs.ReadByte();
            if (header < 0) break;
            int type = header & 0x7F;
            if (fs.Read(lenBytes) != 3) break;
            int length = (lenBytes[0] << 16) | (lenBytes[1] << 8) | lenBytes[2];
            if (length < 0 || fs.Position + length > fs.Length) return 0;

            if (type == 0 && length >= 18)
            {
                byte[] payload = new byte[length];
                if (fs.Read(payload, 0, payload.Length) != payload.Length) return 0;
                ulong packed = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(10, 8));
                uint sampleRate = (uint)(packed >> 44);
                ulong totalSamples = packed & 0x0000000FFFFFFFFFUL;
                return sampleRate > 0 && totalSamples > 0 ? totalSamples / (double)sampleRate : 0;
            }

            fs.Position += length;
            if ((header & 0x80) != 0) break;
        }
        return 0;
    }

    private static double ReadOggDuration(string path)
    {
        using var fs = File.OpenRead(path);
        string codec = "";
        uint sampleRate = 0;
        ushort opusPreSkip = 0;
        ulong lastGranule = 0;
        bool hasGranule = false;
        using var firstPacket = new MemoryStream();
        bool firstPacketComplete = false;

        var header = new byte[27];
        while (fs.Position + 27 <= fs.Length)
        {
            if (fs.Read(header, 0, 27) != 27) break;
            if (Encoding.ASCII.GetString(header, 0, 4) != "OggS") return 0;

            ulong granule = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(6, 8));
            if (granule != ulong.MaxValue)
            {
                lastGranule = granule;
                hasGranule = true;
            }

            int segmentCount = header[26];
            byte[] lacing = new byte[segmentCount];
            if (fs.Read(lacing, 0, segmentCount) != segmentCount) return 0;
            int bodyLength = 0;
            foreach (byte value in lacing) bodyLength += value;
            byte[] body = new byte[bodyLength];
            if (fs.Read(body, 0, bodyLength) != bodyLength) return 0;

            if (!firstPacketComplete)
            {
                int offset = 0;
                foreach (byte segment in lacing)
                {
                    if (offset + segment > body.Length) return 0;
                    firstPacket.Write(body, offset, segment);
                    offset += segment;
                    if (segment < 255)
                    {
                        firstPacketComplete = true;
                        byte[] packet = firstPacket.ToArray();
                        if (packet.Length >= 19 && Encoding.ASCII.GetString(packet, 0, 8) == "OpusHead")
                        {
                            codec = "opus";
                            opusPreSkip = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10, 2));
                            sampleRate = 48000;
                        }
                        else if (packet.Length >= 16 && packet[0] == 1 && Encoding.ASCII.GetString(packet, 1, 6) == "vorbis")
                        {
                            codec = "vorbis";
                            sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4));
                        }
                        break;
                    }
                }
            }
        }

        if (!hasGranule || sampleRate == 0) return 0;
        if (codec == "opus")
        {
            ulong samples = lastGranule > opusPreSkip ? lastGranule - opusPreSkip : lastGranule;
            return samples / 48000.0;
        }
        if (codec == "vorbis") return lastGranule / (double)sampleRate;
        return 0;
    }

    private static double ReadAiffDuration(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        if (fs.Length < 12 || new string(br.ReadChars(4)) != "FORM") return 0;
        ReadUInt32BigEndian(br);
        string form = new(br.ReadChars(4));
        if (form != "AIFF" && form != "AIFC") return 0;

        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(br.ReadChars(4));
            uint size = ReadUInt32BigEndian(br);
            long payload = fs.Position;
            long next = payload + size + (size & 1);
            if (next < payload || next > fs.Length + 1) break;

            if (id == "COMM" && size >= 18)
            {
                ReadUInt16BigEndian(br);
                uint frames = ReadUInt32BigEndian(br);
                ReadUInt16BigEndian(br);
                byte[] extended = br.ReadBytes(10);
                double rate = ReadExtended80(extended);
                return rate > 0 ? frames / rate : 0;
            }
            fs.Position = Math.Min(next, fs.Length);
        }
        return 0;
    }

    private static double ReadMpegAudioDuration(string path)
    {
        using var fs = File.OpenRead(path);
        long start = SkipId3v2(fs);
        fs.Position = start;

        int b0;
        while ((b0 = fs.ReadByte()) >= 0)
        {
            if (b0 != 0xFF) continue;
            int b1 = fs.ReadByte();
            int b2 = fs.ReadByte();
            int b3 = fs.ReadByte();
            if (b3 < 0) break;
            uint h = ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | (uint)b3;
            if ((h & 0xFFE00000) != 0xFFE00000) { fs.Position -= 3; continue; }

            int versionBits = (int)((h >> 19) & 3);
            int layerBits = (int)((h >> 17) & 3);
            int bitrateIndex = (int)((h >> 12) & 15);
            int sampleRateIndex = (int)((h >> 10) & 3);
            if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            { fs.Position -= 3; continue; }

            bool mpeg1 = versionBits == 3;
            int layer = 4 - layerBits;
            int[] srBase = { 44100, 48000, 32000 };
            int sampleRate = srBase[sampleRateIndex];
            if (versionBits == 2) sampleRate /= 2;
            else if (versionBits == 0) sampleRate /= 4;

            int bitrate = GetMpegBitrateKbps(mpeg1, layer, bitrateIndex);
            if (bitrate <= 0 || sampleRate <= 0) return 0;

            long audioBytes = fs.Length - start;
            return audioBytes * 8.0 / (bitrate * 1000.0);
        }
        return 0;
    }

    private static double ReadIsoBaseMediaDuration(string path)
    {
        using var fs = File.OpenRead(path);
        long pos = 0;
        while (pos + 8 <= fs.Length)
        {
            if (!TryReadMp4BoxHeader(fs, pos, fs.Length, out long boxSize, out string type, out long headerSize)) break;
            if (type == "moov")
            {
                long child = pos + headerSize;
                long end = Math.Min(fs.Length, pos + boxSize);
                while (child + 8 <= end)
                {
                    if (!TryReadMp4BoxHeader(fs, child, end, out long childSize, out string childType, out long childHeader)) break;
                    if (childType == "mvhd")
                    {
                        fs.Position = child + childHeader;
                        int version = fs.ReadByte();
                        if (version < 0) return 0;
                        fs.Position += 3; // flags
                        if (version == 1)
                        {
                            fs.Position += 16; // creation + modification
                            uint scale = ReadUInt32BigEndian(fs);
                            ulong duration = ReadUInt64BigEndian(fs);
                            return scale > 0 && duration != ulong.MaxValue ? duration / (double)scale : 0;
                        }
                        else
                        {
                            fs.Position += 8;
                            uint scale = ReadUInt32BigEndian(fs);
                            uint duration = ReadUInt32BigEndian(fs);
                            return scale > 0 && duration != uint.MaxValue ? duration / (double)scale : 0;
                        }
                    }
                    child += childSize;
                }
                return 0;
            }
            pos += boxSize;
        }
        return 0;
    }

    private static bool TryReadMp4BoxHeader(Stream stream, long pos, long limit, out long boxSize, out string type, out long headerSize)
    {
        boxSize = 0;
        type = "";
        headerSize = 8;
        if (pos < 0 || pos + 8 > limit) return false;
        stream.Position = pos;
        uint size32 = ReadUInt32BigEndian(stream);
        Span<byte> t = stackalloc byte[4];
        if (stream.Read(t) != 4) return false;
        type = Encoding.ASCII.GetString(t);
        if (size32 == 1)
        {
            if (pos + 16 > limit) return false;
            boxSize = checked((long)ReadUInt64BigEndian(stream));
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            boxSize = limit - pos;
        }
        else boxSize = size32;

        return boxSize >= headerSize && pos + boxSize <= limit;
    }

    private static double ReadAdtsAacDuration(string path)
    {
        using var fs = File.OpenRead(path);
        long start = SkipId3v2(fs);
        fs.Position = start;
        long samples = 0;
        int sampleRate = 0;
        int frames = 0;
        byte[] h = new byte[7];

        while (fs.Position + 7 <= fs.Length)
        {
            long frameStart = fs.Position;
            if (fs.Read(h, 0, 7) != 7) break;
            if (h[0] != 0xFF || (h[1] & 0xF6) != 0xF0)
            {
                fs.Position = frameStart + 1;
                continue;
            }

            int sfIndex = (h[2] >> 2) & 0x0F;
            int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
            if (sfIndex >= rates.Length) { fs.Position = frameStart + 1; continue; }
            int rate = rates[sfIndex];
            int frameLength = ((h[3] & 0x03) << 11) | (h[4] << 3) | ((h[5] >> 5) & 0x07);
            if (frameLength < 7 || frameStart + frameLength > fs.Length) break;

            int blocks = (h[6] & 0x03) + 1;
            if (sampleRate == 0) sampleRate = rate;
            samples += 1024L * blocks;
            frames++;
            fs.Position = frameStart + frameLength;
        }

        return frames > 0 && sampleRate > 0 ? samples / (double)sampleRate : 0;
    }

    private static double ReadAsfDuration(string path)
    {
        // ASF File Properties Object GUID: 8CABDCA1-A947-11CF-8EE4-00C00C205365
        byte[] guid = new Guid("8CABDCA1-A947-11CF-8EE4-00C00C205365").ToByteArray();
        using var fs = File.OpenRead(path);
        long limit = Math.Min(fs.Length, 4L * 1024 * 1024);
        byte[] buffer = new byte[64 * 1024];
        long absolute = 0;
        int overlap = guid.Length - 1;
        byte[] carry = Array.Empty<byte>();

        while (absolute < limit)
        {
            int toRead = (int)Math.Min(buffer.Length, limit - absolute);
            int read = fs.Read(buffer, 0, toRead);
            if (read <= 0) break;
            byte[] block = new byte[carry.Length + read];
            if (carry.Length > 0) Buffer.BlockCopy(carry, 0, block, 0, carry.Length);
            Buffer.BlockCopy(buffer, 0, block, carry.Length, read);

            int idx = IndexOf(block, guid);
            if (idx >= 0)
            {
                long objectStart = absolute - carry.Length + idx;
                if (objectStart + 88 <= fs.Length)
                {
                    fs.Position = objectStart + 64;
                    Span<byte> b = stackalloc byte[8];
                    if (fs.Read(b) != 8) return 0;
                    ulong playDuration = BinaryPrimitives.ReadUInt64LittleEndian(b);
                    fs.Position = objectStart + 80;
                    if (fs.Read(b) != 8) return 0;
                    ulong prerollMs = BinaryPrimitives.ReadUInt64LittleEndian(b);
                    double seconds = playDuration / 10_000_000.0 - prerollMs / 1000.0;
                    return Math.Max(0, seconds);
                }
            }

            int keep = Math.Min(overlap, block.Length);
            carry = new byte[keep];
            Buffer.BlockCopy(block, block.Length - keep, carry, 0, keep);
            absolute += read;
            fs.Position = absolute;
        }
        return 0;
    }

    private static double ReadApeDuration(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) != 4 || Encoding.ASCII.GetString(magic) != "MAC ") return 0;
        Span<byte> v = stackalloc byte[2];
        if (fs.Read(v) != 2) return 0;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(v);
        if (version < 3980) return 0; // las cabeceras antiguas se dejan a Windows/FFmpeg

        Span<byte> u32 = stackalloc byte[4];
        if (fs.Read(u32) != 4) return 0;
        uint descriptorBytes = BinaryPrimitives.ReadUInt32LittleEndian(u32);
        if (descriptorBytes < 52 || descriptorBytes > fs.Length) return 0;
        if (fs.Read(u32) != 4) return 0;
        uint headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(u32);
        if (headerBytes < 24) return 0;

        fs.Position = descriptorBytes;
        fs.Position += 4; // compression + flags
        uint blocksPerFrame = ReadUInt32LittleEndian(fs);
        uint finalFrameBlocks = ReadUInt32LittleEndian(fs);
        uint totalFrames = ReadUInt32LittleEndian(fs);
        fs.Position += 4; // bits/sample + channels
        uint sampleRate = ReadUInt32LittleEndian(fs);
        if (sampleRate == 0 || totalFrames == 0) return 0;
        ulong totalSamples = (ulong)blocksPerFrame * (totalFrames - 1UL) + finalFrameBlocks;
        return totalSamples / (double)sampleRate;
    }

    private static double ReadWavPackDuration(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> hdr = stackalloc byte[32];
        if (fs.Read(hdr) != 32 || Encoding.ASCII.GetString(hdr[..4]) != "wvpk") return 0;
        uint totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(24, 4));
        int rateIndex = (int)((flags >> 23) & 0x0F);
        int[] rates = { 6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 192000, 0 };
        int sampleRate = rates[rateIndex];
        return sampleRate > 0 && totalSamples != uint.MaxValue ? totalSamples / (double)sampleRate : 0;
    }

    private static double ReadAmrDuration(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] nbMagic = Encoding.ASCII.GetBytes("#!AMR\n");
        byte[] wbMagic = Encoding.ASCII.GetBytes("#!AMR-WB\n");
        byte[] start = new byte[wbMagic.Length];
        int read = fs.Read(start, 0, start.Length);
        bool wide;
        if (read >= wbMagic.Length && start.AsSpan(0, wbMagic.Length).SequenceEqual(wbMagic))
        {
            wide = true;
            fs.Position = wbMagic.Length;
        }
        else if (read >= nbMagic.Length && start.AsSpan(0, nbMagic.Length).SequenceEqual(nbMagic))
        {
            wide = false;
            fs.Position = nbMagic.Length;
        }
        else return 0;

        int[] nbSizes = { 13,14,16,18,20,21,27,32,6,1,1,1,1,1,1,1 };
        int[] wbSizes = { 18,24,33,37,41,47,51,59,61,6,1,1,1,1,1,1 };
        long frames = 0;
        while (fs.Position < fs.Length)
        {
            int h = fs.ReadByte();
            if (h < 0) break;
            int ft = (h >> 3) & 0x0F;
            int frameBytes = (wide ? wbSizes : nbSizes)[ft];
            if (frameBytes <= 0) break;
            fs.Position += frameBytes - 1L;
            frames++;
        }
        return frames * 0.020;
    }

    private static double ReadAuDuration(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (fs.Read(header) != 24 || Encoding.ASCII.GetString(header[..4]) != ".snd") return 0;
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4));
        uint encoding = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4));
        uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        uint channels = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (sampleRate == 0 || channels == 0 || dataOffset >= fs.Length) return 0;
        long bytes = dataSize == uint.MaxValue ? fs.Length - dataOffset : Math.Min(dataSize, fs.Length - dataOffset);
        int bytesPerSample = encoding switch { 2 => 1, 3 => 2, 4 => 3, 5 => 4, 6 => 4, 7 => 8, _ => 0 };
        return bytesPerSample > 0 ? bytes / (double)(bytesPerSample * channels * sampleRate) : 0;
    }

    private static int GetMpegBitrateKbps(bool mpeg1, int layer, int index)
    {
        int[] mpeg1L1 = { 0,32,64,96,128,160,192,224,256,288,320,352,384,416,448,0 };
        int[] mpeg1L2 = { 0,32,48,56,64,80,96,112,128,160,192,224,256,320,384,0 };
        int[] mpeg1L3 = { 0,32,40,48,56,64,80,96,112,128,160,192,224,256,320,0 };
        int[] mpeg2L1 = { 0,32,48,56,64,80,96,112,128,144,160,176,192,224,256,0 };
        int[] mpeg2L23 = { 0,8,16,24,32,40,48,56,64,80,96,112,128,144,160,0 };
        return mpeg1
            ? (layer == 1 ? mpeg1L1[index] : layer == 2 ? mpeg1L2[index] : mpeg1L3[index])
            : (layer == 1 ? mpeg2L1[index] : mpeg2L23[index]);
    }

    private static long SkipId3v2(Stream stream)
    {
        long start = stream.Position;
        Span<byte> hdr = stackalloc byte[10];
        if (stream.Read(hdr) != 10) { stream.Position = start; return start; }
        if (hdr[0] != (byte)'I' || hdr[1] != (byte)'D' || hdr[2] != (byte)'3')
        { stream.Position = start; return start; }
        int size = ((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F);
        long pos = Math.Min(stream.Length, start + 10L + size);
        stream.Position = pos;
        return pos;
    }

    private static ushort ReadUInt16BigEndian(BinaryReader br)
    {
        Span<byte> b = stackalloc byte[2];
        if (br.Read(b) != 2) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static uint ReadUInt32BigEndian(BinaryReader br)
    {
        Span<byte> b = stackalloc byte[4];
        if (br.Read(b) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> b = stackalloc byte[4];
        if (stream.Read(b) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static ulong ReadUInt64BigEndian(Stream stream)
    {
        Span<byte> b = stackalloc byte[8];
        if (stream.Read(b) != 8) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt64BigEndian(b);
    }

    private static uint ReadUInt32LittleEndian(Stream stream)
    {
        Span<byte> b = stackalloc byte[4];
        if (stream.Read(b) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static double ReadExtended80(byte[] bytes)
    {
        if (bytes.Length < 10) return 0;
        int exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        bool negative = (bytes[0] & 0x80) != 0;
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(2, 8));
        if (exponent == 0 && mantissa == 0) return 0;
        if (exponent == 0x7FFF) return double.NaN;
        double fraction = mantissa / Math.Pow(2.0, 63);
        double value = fraction * Math.Pow(2.0, exponent - 16383);
        return negative ? -value : value;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++) if (haystack[i + j] != needle[j]) break;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static string? NormalizeExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        try { return File.Exists(executable) ? Path.GetFullPath(executable) : null; }
        catch { return null; }
    }

    private static string? FindFfprobe(string? ffmpegPath)
    {
        var candidates = new List<string>();
        string? ffmpeg = NormalizeExecutable(ffmpegPath) ?? AudioConversionService.FindFfmpeg();
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            string? dir = Path.GetDirectoryName(ffmpeg);
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir, "ffprobe.exe"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffprobe.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffprobe.exe"));

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string item in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                candidates.Add(Path.Combine(item.Trim('"'), "ffprobe.exe"));
        }
        return candidates.Find(File.Exists);
    }

    private static async Task<double> ProbeWithFfprobeAsync(string ffprobe, string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi);
        if (p is null) return 0;
        string outputTask = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);
        if (p.ExitCode != 0) return 0;
        return double.TryParse(outputTask.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? seconds : 0;
    }

    private static async Task<double> ProbeWithFfmpegAsync(string ffmpeg, string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "-hide_banner", "-i", path }) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi);
        if (p is null) return 0;
        string error = await p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);
        Match match = FfmpegDurationRegex.Match(error);
        if (!match.Success) return 0;
        if (!int.TryParse(match.Groups["h"].Value, out int h)) return 0;
        if (!int.TryParse(match.Groups["m"].Value, out int m)) return 0;
        string secondsText = match.Groups["s"].Value.Replace(',', '.');
        if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return 0;
        return h * 3600.0 + m * 60.0 + s;
    }

    // PKEY_Media_Duration: UInt64 en unidades de 100 ns.
    private static readonly PropertyKey PkeyMediaDuration = new(
        new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

    private static bool TryReadWindowsShellDuration(string path, out double seconds)
    {
        seconds = 0;
        if (!OperatingSystem.IsWindows()) return false;

        Guid iid = typeof(IPropertyStore).GUID;
        IPropertyStore? store = null;
        PropVariant pv = default;
        try
        {
            int hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
            if (hr < 0 || store is null) return false;
            PropertyKey key = PkeyMediaDuration;
            hr = store.GetValue(ref key, out pv);
            if (hr < 0) return false;

            const ushort VT_UI8 = 21;
            const ushort VT_I8 = 20;
            long ticks100ns;
            if (pv.VarType == VT_UI8) ticks100ns = unchecked((long)pv.UInt64Value);
            else if (pv.VarType == VT_I8) ticks100ns = pv.Int64Value;
            else return false;

            if (ticks100ns <= 0) return false;
            seconds = ticks100ns / 10_000_000.0;
            return seconds > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { PropVariantClear(ref pv); } catch { }
            if (store is not null && Marshal.IsComObject(store)) Marshal.FinalReleaseComObject(store);
        }
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public ulong UInt64Value;
        [FieldOffset(8)] public long Int64Value;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindContext,
        uint flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
