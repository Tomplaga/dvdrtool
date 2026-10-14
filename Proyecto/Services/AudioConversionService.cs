using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

/// <summary>
/// Convierte archivos de audio a WAV PCM 44.1 kHz, 16-bit, estéreo,
/// formato requerido físicamente por CD-DA. Acepta fuentes con otras
/// frecuencias/profundidades: FFmpeg es la ruta más amplia y el conversor
/// PCM interno normaliza WAV/decodificados comunes incluso sin FFmpeg.
/// </summary>
public sealed class AudioConversionService
{
    public static string OpenFileFilter =>
        "Audio (formatos comunes)|*.wav;*.wave;*.mp3;*.mp2;*.mp1;*.flac;*.ogg;*.oga;*.opus;*.ape;*.m4a;*.m4b;*.aac;*.wma;*.asf;*.aiff;*.aif;*.aifc;*.ac3;*.eac3;*.dts;*.wv;*.amr;*.awb;*.au;*.snd;*.caf;*.mka;*.webm;*.mp4;*.mov;*.3gp;*.3g2;*.tak;*.tta;*.mpc;*.ofr;*.ofs;*.spx;*.ra;*.rm;*.voc|" +
        "Todos los archivos (*.*)|*.*";

    // No cerramos la lista por extensión: FFmpeg es el conversor principal.
    // Si FFmpeg puede decodificar el archivo, dvdrtool puede convertirlo a CD-DA.
    // Los decodificadores antiguos quedan únicamente como respaldo.
    public static bool IsSupportedSource(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public static string? FindFfmpeg(string? cdrtoolsDirectory = null)
    {
        var candidates = new List<string>();
        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDir, "tools", "sound", "ffmpeg.exe"));

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        AddIfRoot(candidates, localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        AddIfRoot(candidates, userProfile, "scoop", "shims", "ffmpeg.exe");
        AddIfRoot(candidates, commonAppData, "chocolatey", "bin", "ffmpeg.exe");
        AddIfRoot(candidates, programFiles, "ffmpeg", "bin", "ffmpeg.exe");
        AddIfRoot(candidates, programFilesX86, "ffmpeg", "bin", "ffmpeg.exe");
        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");

        if (!string.IsNullOrWhiteSpace(cdrtoolsDirectory))
        {
            try
            {
                string? toolsRoot = Directory.GetParent(Path.GetFullPath(cdrtoolsDirectory))?.FullName;
                if (!string.IsNullOrWhiteSpace(toolsRoot))
                {
                    candidates.Add(Path.Combine(toolsRoot, "ffmpeg", "ffmpeg.exe"));
                    candidates.Add(Path.Combine(toolsRoot, "ffmpeg", "bin", "ffmpeg.exe"));
                    candidates.Add(Path.Combine(toolsRoot, "sound", "ffmpeg.exe"));
                }
            }
            catch { }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string item in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                candidates.Add(Path.Combine(item.Trim('"'), "ffmpeg.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<string> GetAvailableCdrtfeFormats(string? cdrtoolsDirectory)
    {
        var formats = new List<string> { "WAV CD-DA" };
        string? sound = FindCdrtfeSoundDirectory(cdrtoolsDirectory);
        if (sound is null) return formats;
        if (File.Exists(Path.Combine(sound, "mpg123.exe"))) formats.Add("MP1/MP2/MP3");
        if (File.Exists(Path.Combine(sound, "flac.exe"))) formats.Add("FLAC");
        if (File.Exists(Path.Combine(sound, "oggdec.exe"))) formats.Add("OGG/OGA");
        if (File.Exists(Path.Combine(sound, "mac.exe"))) formats.Add("APE");
        if (File.Exists(Path.Combine(sound, "opusdec.exe"))) formats.Add("Opus");
        return formats;
    }

    public static bool HasCdrtfeAudioTools(string? cdrtoolsDirectory) =>
        GetAvailableCdrtfeFormats(cdrtoolsDirectory).Count > 1;

    public async Task ConvertToMp3Async(
        string source,
        string target,
        string cdrtoolsDirectory,
        string? ffmpegPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("No se encontró el archivo de audio.", source);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);

        // Si ya es MP3, se conserva el archivo original sin volver a codificarlo.
        if (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            log?.Report($"MP3/datos: copiando {Path.GetFileName(source)} sin recodificar.");
            File.Copy(source, target, overwrite: true);
            return;
        }

        string? ffmpeg = NormalizeExecutable(ffmpegPath) ?? FindFfmpeg(cdrtoolsDirectory);
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            throw new InvalidOperationException(
                $"Para mover '{Path.GetFileName(source)}' a MP3 se necesita FFmpeg. " +
                "Los archivos .mp3 existentes se copian directamente; otros formatos se convierten a MP3 de 192 kbps. " +
                "Selecciona ffmpeg.exe en Conversión de audio.");
        }

        log?.Report($"MP3/datos: convirtiendo {Path.GetFileName(source)} a MP3 192 kbps con FFmpeg.");
        try
        {
            await RunToolAsync(
                ffmpeg,
                new[] { "-hide_banner", "-loglevel", "warning", "-y", "-i", source, "-vn", "-map_metadata", "0", "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "192k", target },
                Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory,
                log,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Algunas compilaciones de FFmpeg no exponen libmp3lame por nombre.
            // Se intenta el codificador MP3 que la compilación tenga como predeterminado.
            TryDelete(target);
            await RunToolAsync(
                ffmpeg,
                new[] { "-hide_banner", "-loglevel", "warning", "-y", "-i", source, "-vn", "-map_metadata", "0", "-ac", "2", "-ar", "44100", "-c:a", "mp3", "-b:a", "192k", target },
                Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory,
                log,
                cancellationToken);
        }
    }

    public static string NormalizeDataAudioFormat(string? format)
    {
        string value = (format ?? "Original").Trim();
        if (value.StartsWith("Original", StringComparison.OrdinalIgnoreCase)) return "Original";
        if (value.StartsWith("MP3", StringComparison.OrdinalIgnoreCase)) return "MP3";
        if (value.StartsWith("AAC", StringComparison.OrdinalIgnoreCase) || value.Contains("M4A", StringComparison.OrdinalIgnoreCase)) return "AAC / M4A";
        if (value.StartsWith("WMA", StringComparison.OrdinalIgnoreCase)) return "WMA";
        if (value.StartsWith("Opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (value.StartsWith("OGG", StringComparison.OrdinalIgnoreCase)) return "OGG Vorbis";
        if (value.StartsWith("FLAC", StringComparison.OrdinalIgnoreCase)) return "FLAC";
        return "Original";
    }

    public static string GetDataAudioExtension(string? format, string sourcePath)
    {
        return NormalizeDataAudioFormat(format) switch
        {
            "MP3" => ".mp3",
            "AAC / M4A" => ".m4a",
            "WMA" => ".wma",
            "Opus" => ".opus",
            "OGG Vorbis" => ".ogg",
            "FLAC" => ".flac",
            _ => string.IsNullOrWhiteSpace(Path.GetExtension(sourcePath)) ? ".audio" : Path.GetExtension(sourcePath)
        };
    }

    /// <summary>
    /// Prepara un archivo para la sesión de datos. "Original" copia sin recodificar
    /// y por tanto no necesita FFmpeg. Los demás formatos usan FFmpeg.
    /// </summary>
    public async Task PrepareForDataAudioAsync(
        string source,
        string target,
        string format,
        string cdrtoolsDirectory,
        string? ffmpegPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("No se encontró el archivo de audio.", source);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);

        string normalized = NormalizeDataAudioFormat(format);
        if (normalized == "Original")
        {
            log?.Report($"Audio/datos: copiando {Path.GetFileName(source)} en su formato original, sin convertir.");
            File.Copy(source, target, overwrite: true);
            return;
        }

        string sourceExtension = Path.GetExtension(source);
        string targetExtension = GetDataAudioExtension(normalized, source);
        bool sameFormat = normalized switch
        {
            "MP3" => sourceExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase),
            "AAC / M4A" => sourceExtension.Equals(".m4a", StringComparison.OrdinalIgnoreCase),
            "WMA" => sourceExtension.Equals(".wma", StringComparison.OrdinalIgnoreCase),
            "Opus" => sourceExtension.Equals(".opus", StringComparison.OrdinalIgnoreCase),
            "OGG Vorbis" => sourceExtension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || sourceExtension.Equals(".oga", StringComparison.OrdinalIgnoreCase),
            "FLAC" => sourceExtension.Equals(".flac", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        if (sameFormat)
        {
            log?.Report($"Audio/datos: {Path.GetFileName(source)} ya está en {normalized}; se copia sin recodificar.");
            File.Copy(source, target, overwrite: true);
            return;
        }

        string? ffmpeg = NormalizeExecutable(ffmpegPath) ?? FindFfmpeg(cdrtoolsDirectory);
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            throw new InvalidOperationException(
                $"Para convertir '{Path.GetFileName(source)}' a {normalized} se necesita FFmpeg. " +
                "Puedes elegir 'Original (sin convertir)' para guardarlo en la sesión de datos sin FFmpeg, " +
                "o seleccionar ffmpeg.exe en Conversión de audio.");
        }

        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-i", source, "-vn", "-map_metadata", "0" };
        switch (normalized)
        {
            case "MP3":
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "192k" });
                break;
            case "AAC / M4A":
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                break;
            case "WMA":
                args.AddRange(new[] { "-c:a", "wmav2", "-b:a", "192k" });
                break;
            case "Opus":
                args.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" });
                break;
            case "OGG Vorbis":
                args.AddRange(new[] { "-c:a", "libvorbis", "-q:a", "5" });
                break;
            case "FLAC":
                args.AddRange(new[] { "-c:a", "flac" });
                break;
            default:
                throw new InvalidOperationException($"Formato de datos no reconocido: {normalized}.");
        }
        args.Add(target);

        log?.Report($"Audio/datos: convirtiendo {Path.GetFileName(source)} a {normalized} con FFmpeg.");
        try
        {
            await RunToolAsync(ffmpeg, args, Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory, log, cancellationToken);
        }
        catch (InvalidOperationException) when (normalized == "MP3")
        {
            TryDelete(target);
            args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-i", source, "-vn", "-map_metadata", "0", "-c:a", "mp3", "-b:a", "192k", target };
            await RunToolAsync(ffmpeg, args, Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory, log, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> PrepareForCddaAsync(
        IReadOnlyList<string> sourceFiles,
        string cdrtoolsDirectory,
        string? ffmpegPath,
        string tempDirectory,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceFiles.Count == 0) throw new InvalidOperationException("Agrega al menos una pista de audio.");
        Directory.CreateDirectory(tempDirectory);

        var result = new List<string>(sourceFiles.Count);
        for (int i = 0; i < sourceFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = sourceFiles[i];
            if (!File.Exists(source)) throw new FileNotFoundException("No se encontró una pista de audio.", source);
            double baseProgress = sourceFiles.Count == 0 ? 0 : (double)i / sourceFiles.Count;
            progress?.Report((baseProgress, $"Preparando audio {i + 1}/{sourceFiles.Count}: {Path.GetFileName(source)}"));

            if ((Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                 || Path.GetExtension(source).Equals(".wave", StringComparison.OrdinalIgnoreCase))
                && WavHelper.IsCddaWav(source, out _))
            {
                log?.Report($"Audio: {Path.GetFileName(source)} ya está en formato CD-DA; no se convierte.");
                result.Add(source);
                continue;
            }

            string target = Path.Combine(tempDirectory, $"track_{i + 1:00}.wav");
            await ConvertOneAsync(source, target, cdrtoolsDirectory, ffmpegPath, log, cancellationToken);
            WavHelper.ValidateCddaWav(target);
            result.Add(target);
        }

        progress?.Report((1.0, "Audio preparado para CD-DA."));
        return result;
    }

    private static async Task ConvertOneAsync(
        string source,
        string target,
        string cdrtoolsDirectory,
        string? ffmpegPath,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        string? ffmpeg = NormalizeExecutable(ffmpegPath) ?? FindFfmpeg(cdrtoolsDirectory);

        // FFmpeg ofrece la ruta más uniforme: decodifica, remuestrea y fuerza PCM s16 estéreo.
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            log?.Report($"Audio: convirtiendo {Path.GetFileName(source)} con FFmpeg a 44.1 kHz / 16-bit / estéreo.");
            await RunToolAsync(
                ffmpeg,
                new[] { "-hide_banner", "-loglevel", "warning", "-y", "-i", source, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", target },
                Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory,
                log,
                cancellationToken);
            return;
        }

        if (extension is ".wav" or ".wave")
        {
            log?.Report($"Audio: normalizando {Path.GetFileName(source)} con el conversor PCM interno a 44.1 kHz / 16-bit / estéreo.");
            PcmWavNormalizer.ConvertToCdda(source, target);
            return;
        }

        string? soundDir = FindCdrtfeSoundDirectory(cdrtoolsDirectory);
        if (soundDir is null)
            throw new InvalidOperationException(BuildMissingDecoderMessage(extension));

        switch (extension)
        {
            case ".mp1":
            case ".mp2":
            case ".mp3":
            {
                string tool = RequireTool(soundDir, "mpg123.exe", extension);
                log?.Report($"Audio: convirtiendo {Path.GetFileName(source)} con mpg123 (cdrtfe).");
                await RunToolAsync(tool,
                    new[] { "--rate", "44100", "--stereo", "--buffer", "3072", "--resync", "-w", target, source },
                    soundDir, log, cancellationToken);
                break;
            }
            case ".flac":
            {
                string tool = RequireTool(soundDir, "flac.exe", extension);
                log?.Report($"Audio: decodificando {Path.GetFileName(source)} con FLAC (cdrtfe).");
                await RunToolAsync(tool, new[] { "-d", "-f", "-o", target, source }, soundDir, log, cancellationToken);
                break;
            }
            case ".ogg":
            case ".oga":
            {
                string tool = RequireTool(soundDir, "oggdec.exe", extension);
                log?.Report($"Audio: decodificando {Path.GetFileName(source)} con oggdec (cdrtfe).");
                await RunToolAsync(tool, new[] { "--quiet", "-b16", "-o", target, source }, soundDir, log, cancellationToken);
                break;
            }
            case ".ape":
            {
                string tool = RequireTool(soundDir, "mac.exe", extension);
                log?.Report($"Audio: decodificando {Path.GetFileName(source)} con Monkey's Audio (cdrtfe).");
                await RunToolAsync(tool, new[] { source, target, "-d" }, soundDir, log, cancellationToken);
                break;
            }
            case ".opus":
            {
                string tool = RequireTool(soundDir, "opusdec.exe", extension);
                log?.Report($"Audio: decodificando {Path.GetFileName(source)} con opusdec.");
                await RunToolAsync(tool, new[] { "--quiet", source, target }, soundDir, log, cancellationToken);
                break;
            }
            default:
                throw new InvalidOperationException(BuildMissingDecoderMessage(extension));
        }

        if (!WavHelper.IsCddaWav(target, out string? reason))
        {
            string normalized = target + ".cdda.wav";
            try
            {
                log?.Report($"Audio: el decodificador entregó {reason} Normalizando automáticamente con el conversor PCM interno.");
                PcmWavNormalizer.ConvertToCdda(target, normalized);
                TryDelete(target);
                File.Move(normalized, target);
            }
            catch (Exception ex)
            {
                TryDelete(normalized);
                TryDelete(target);
                throw new InvalidDataException(
                    $"{Path.GetFileName(source)} se pudo decodificar, pero su WAV intermedio no pudo normalizarse a CD Audio. " +
                    $"Detalle: {ex.Message} Para códecs WAV no PCM/float, selecciona FFmpeg.", ex);
            }
        }
    }

    private static string? FindCdrtfeSoundDirectory(string? cdrtoolsDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(cdrtoolsDirectory))
        {
            try
            {
                string full = Path.GetFullPath(cdrtoolsDirectory);
                string? toolsRoot = Directory.GetParent(full)?.FullName;
                if (!string.IsNullOrWhiteSpace(toolsRoot)) candidates.Add(Path.Combine(toolsRoot, "sound"));
            }
            catch { }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "sound"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "sound"));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string RequireTool(string soundDir, string fileName, string extension)
    {
        string tool = Path.Combine(soundDir, fileName);
        if (!File.Exists(tool))
            throw new FileNotFoundException(
                $"Falta {fileName} para convertir archivos {extension}. Instala la versión completa de cdrtfe o usa FFmpeg.", tool);
        return tool;
    }

    private static string BuildMissingDecoderMessage(string extension)
    {
        return $"No se pudo convertir {extension} porque FFmpeg no está disponible. " +
               "Instala FFmpeg automáticamente desde dvdrtool para admitir cualquier formato que FFmpeg pueda decodificar. " +
               "Los decodificadores antiguos para WAV/MP3/FLAC/OGG/APE/Opus se usan solo como respaldo.";
    }

    private static void AddIfRoot(List<string> candidates, string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        string value = root;
        foreach (string part in parts) value = Path.Combine(value, part);
        candidates.Add(value);
    }

    private static string? NormalizeExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        try { return File.Exists(executable) ? Path.GetFullPath(executable) : null; }
        catch { return null; }
    }

    private static async Task RunToolAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var args = arguments.ToList();
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        log?.Report($"> {Path.GetFileName(executable)} {FormatArguments(args)}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            stdout.Enqueue(e.Data);
            log?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            stderr.Enqueue(e.Data);
            log?.Report(e.Data);
        };

        if (!process.Start()) throw new InvalidOperationException($"No se pudo iniciar {Path.GetFileName(executable)}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string detail = string.Join(Environment.NewLine, stderr.Concat(stdout).Reverse().Take(12).Reverse());
            if (string.IsNullOrWhiteSpace(detail)) detail = $"Código de salida: {process.ExitCode}.";
            throw new InvalidOperationException($"{Path.GetFileName(executable)} terminó con error.{Environment.NewLine}{detail}");
        }
    }

    private static string FormatArguments(IEnumerable<string> args) =>
        string.Join(" ", args.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
