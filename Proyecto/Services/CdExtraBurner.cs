using CdExtraStudio.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

/// <summary>
/// Backend de grabación basado en cdrtools, el mismo conjunto de utilidades
/// que utiliza cdrtfe: cdrecord para escribir el medio y mkisofs para crear
/// la sesión de datos.
/// </summary>
public sealed class CdExtraBurner
{
    private readonly AudioConversionService _audioConverter = new();
    private static readonly Regex ScanBusRegex = new(
        @"^\s*(?<addr>\d+,\d+,\d+)\s+\d+\)\s+'(?<vendor>[^']*)'\s+'(?<model>[^']*)'\s+'(?<rev>[^']*)'\s+(?<type>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MsInfoRegex = new(
        @"(?m)^\s*(?<first>\d+)\s*,\s*(?<next>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BlocksTotalRegex = new(
        @"Blocks\s+total:\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BlocksRemainingRegex = new(
        @"Blocks\s+remaining:\s*(?<value>-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AtipLeadOutRegex = new(
        @"ATIP\s+start\s+of\s+lead\s+out:\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Algunas compilaciones de cdrecord imprimen el lead-out únicamente como
    // tiempo MSF, por ejemplo: ATIP start of lead out: (79:59/74).
    // En CD el valor LBA equivalente se obtiene restando los 150 sectores
    // iniciales al tiempo MSF absoluto.
    private static readonly Regex AtipLeadOutMsfRegex = new(
        @"ATIP\s+start\s+of\s+lead\s+out:[^\r\n]*\((?<min>\d+):(?<sec>\d+)[/:](?<frame>\d+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Salida habitual de otras versiones de cdrecord -prcap/-minfo:
    // "Disk capacity is: 359849 blocks = ...".
    private static readonly Regex DiskCapacityBlocksRegex = new(
        @"(?:Disk\s+)?capacity\s+(?:is\s*:\s*|:\s*)(?<value>\d+)\s+blocks?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LastLeadOutRegex = new(
        @"last\s+start\s+of\s+lead\s+out:\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EndLbaRegex = new(
        @"End\s+LBA:\s*(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DiskStatusRegex = new(
        @"disk\s+status:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SessionStatusRegex = new(
        @"session\s+status:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MountedMediaClassRegex = new(
        @"Mounted\s+media\s+class:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MountedMediaTypeRegex = new(
        @"Mounted\s+media\s+type:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DriverFlagsRegex = new(
        @"Driver\s+flags\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SupportedModesRegex = new(
        @"Supported\s+modes\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // cdrecord anuncia la velocidad final antes de la cuenta atrás:
    // "Starting to write CD/DVD/BD at speed 8 in real TAO mode..."
    // dvdrtool usa esta línea para evitar escribir a una velocidad distinta
    // de la seleccionada por el usuario.
    private static readonly Regex ActualWriteSpeedRegex = new(
        @"Starting\s+to\s+write.*?\bat\s+speed\s+(?<speed>\d+(?:[\.,]\d+)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // cdrtools README.multi indica ~11250 sectores de lead-out/lead-in para
    // abrir la segunda sesión de un CD-R. Usamos 11400 como margen conservador.
    private const long FirstAdditionalSessionOverheadSectors = 11400;
    // Margen adicional para pequeñas variaciones de padding/pregap y para no grabar al borde físico del medio.
    private const long SafetyMarginSectors = 750; // 10 segundos de CD.

    private sealed record MediaPreflight(
        string MediaClass,
        string MediaType,
        string DiskStatus,
        string SessionStatus,
        bool SupportsTao,
        bool SupportsBurnFree,
        bool SupportsForceSpeed)
    {
        public bool IsBlank => DiskStatus.Contains("empty", StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindToolsDirectory()
    {
        var candidates = new List<string>();
        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "tools", "cdrtools"));
        candidates.Add(Path.Combine(baseDir, "cdrtools"));

        AddProgramFilesCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddProgramFilesCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string item in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                candidates.Add(item.Trim('"'));
        }

        foreach (string candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (!IsValidToolsDirectory(fullPath)) continue;

                // Si se trata del motor portable junto a dvdrtool, no lo consideramos
                // listo hasta que exista también cygwin1.dll. Esto repara instalaciones
                // incompletas creadas por la versión 2.6.4.
                bool isLocalPortable = fullPath.Equals(
                    Path.GetFullPath(Path.Combine(baseDir, "tools", "cdrtools")),
                    StringComparison.OrdinalIgnoreCase);
                if (isLocalPortable && !HasCygwinRuntime(fullPath)) continue;

                return fullPath;
            }
            catch
            {
                // Ignora entradas PATH inválidas y continúa con la siguiente candidata.
            }
        }

        return null;
    }

    public static bool IsValidToolsDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        return File.Exists(Path.Combine(directory, "cdrecord.exe"))
            && File.Exists(Path.Combine(directory, "mkisofs.exe"));
    }

    public static bool HasCygwinRuntime(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        if (File.Exists(Path.Combine(directory, "cygwin1.dll"))) return true;

        string? parent = Directory.GetParent(directory)?.FullName;
        return !string.IsNullOrWhiteSpace(parent)
            && File.Exists(Path.Combine(parent, "cygwin", "cygwin1.dll"));
    }

    public async Task<IReadOnlyList<CdrtoolsDevice>> ScanDevicesAsync(
        string toolsDirectory,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");

        try
        {
            CommandResult sptiResult = await RunToolAsync(
                cdrecord,
                new[] { "-scanbus", "dev=SPTI" },
                toolsDirectory,
                log,
                cancellationToken);

            IReadOnlyList<CdrtoolsDevice> sptiDevices = ParseScanBus(sptiResult.AllOutput, forceSpti: true);
            if (sptiDevices.Count > 0) return sptiDevices;

            log?.Report("No se encontraron unidades con el escaneo SPTI forzado; probando el modo automático de cdrtfe/cdrecord…");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Report($"El escaneo SPTI forzado falló: {ex.Message}");
            log?.Report("Probando cdrecord -scanbus en modo automático…");
        }

        CommandResult automaticResult = await RunToolAsync(
            cdrecord,
            new[] { "-scanbus" },
            toolsDirectory,
            log,
            cancellationToken);

        return ParseScanBus(automaticResult.AllOutput, forceSpti: false);
    }

    private static IReadOnlyList<CdrtoolsDevice> ParseScanBus(string output, bool forceSpti)
    {
        var devices = new List<CdrtoolsDevice>();
        foreach (string line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = ScanBusRegex.Match(line);
            if (!match.Success) continue;

            string type = match.Groups["type"].Value.Trim();
            if (!LooksLikeOpticalDevice(type)) continue;

            string addr = match.Groups["addr"].Value;
            string vendor = match.Groups["vendor"].Value.Trim();
            string model = match.Groups["model"].Value.Trim();
            string rev = match.Groups["rev"].Value.Trim();
            string description = string.Join(" ", new[] { vendor, model, rev }.Where(value => !string.IsNullOrWhiteSpace(value)));

            devices.Add(new CdrtoolsDevice
            {
                BusAddress = addr,
                DeviceSpec = forceSpti ? $"SPTI:{addr}" : addr,
                Description = description
            });
        }

        return devices;
    }

    public async Task<DiscCapacityInfo> AnalyzeCapacityAsync(
        string toolsDirectory,
        string deviceSpec,
        IReadOnlyList<string> audioFiles,
        string dataFolder,
        string volumeLabel,
        string? ffmpegPath,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        if (string.IsNullOrWhiteSpace(deviceSpec)) throw new InvalidOperationException("Selecciona un dispositivo cdrtools.");
        // La medición automática admite una lista de audio vacía para poder mostrar
        // la capacidad del medio desde el primer momento. BurnAsync conserva su
        // validación estricta y exige al menos una pista CD Audio para grabar.
        if (!Directory.Exists(dataFolder)) throw new DirectoryNotFoundException("La carpeta de datos no existe.");

        volumeLabel = SanitizeVolumeLabel(volumeLabel);
        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");
        string mkisofs = Path.Combine(toolsDirectory, "mkisofs.exe");
        string tempProbeIso = Path.Combine(Path.GetTempPath(), $"dvdrtool_Capacity_{Guid.NewGuid():N}.iso");

        try
        {
            progress?.Report((0.03, "Leyendo la duración de las pistas sin convertirlas…"));
            long audioSectors = 0;
            var unknown = new List<string>();

            for (int i = 0; i < audioFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = audioFiles[i];
                if (!File.Exists(source)) throw new FileNotFoundException("No se encontró una pista de audio.", source);

                var measured = await AudioDurationEstimator.TryGetCddaSectorCountAsync(source, ffmpegPath, cancellationToken);
                if (measured.Success)
                {
                    audioSectors += measured.Sectors;
                    log?.Report($"Capacidad audio: {Path.GetFileName(source)} = {measured.Detail}.");
                }
                else
                {
                    unknown.Add(Path.GetFileName(source));
                    log?.Report($"Capacidad audio: duración pendiente para {Path.GetFileName(source)} ({measured.Detail}). No se convertirá durante la estimación automática.");
                }

                double p = 0.03 + ((i + 1) / (double)audioFiles.Count) * 0.34;
                progress?.Report((p, $"Midiendo audio {i + 1}/{audioFiles.Count}: {Path.GetFileName(source)}"));
            }

            progress?.Report((0.42, "Leyendo la capacidad física del CD…"));
            DiscCapacityInfo info = await AnalyzePreparedProjectAsync(
                cdrecord, mkisofs, toolsDirectory, deviceSpec, audioSectors, dataFolder, volumeLabel,
                tempProbeIso, log, cancellationToken,
                audioEstimateComplete: unknown.Count == 0,
                unknownAudioCount: unknown.Count,
                unknownAudioNames: unknown.Count == 0 ? null : string.Join(", ", unknown.Take(5)) + (unknown.Count > 5 ? "…" : ""));

            string finalStatus = !info.AudioEstimateComplete
                ? $"Capacidad parcial: {info.UnknownAudioCount} pista(s) se medirán exactamente al grabar."
                : info.Fits ? "El proyecto cabe en el CD." : "El proyecto excede la capacidad del CD.";
            progress?.Report((1.0, finalStatus));
            return info;
        }
        finally
        {
            TryDelete(tempProbeIso);
        }
    }

    private async Task<MediaPreflight> RunMediaPreflightAsync(
        string cdrecord,
        string toolsDirectory,
        string deviceSpec,
        IProgress<string>? log,
        CancellationToken cancellationToken,
        bool allowDvdData = false,
        bool requireBluRay = false)
    {
        log?.Report(requireBluRay
            ? "Preflight Blu-ray: comprobando grabadora y medio BD-R/BD-RE antes de escribir la imagen…"
            : allowDvdData
                ? "Preflight: comprobando grabadora y medio CD/DVD antes de grabar datos…"
                : "Preflight estable: comprobando grabadora, medio y modo TAO antes de preparar el audio…");

        CommandResult drive = await RunToolAsync(
            cdrecord,
            new[] { "-checkdrive", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken);

        CommandResult media = await RunToolAsync(
            cdrecord,
            new[] { "-minfo", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);

        string combined = string.Join(Environment.NewLine, drive.AllOutput, media.AllOutput);
        string mediaClass = MatchValue(MountedMediaClassRegex, combined);
        string mediaType = MatchValue(MountedMediaTypeRegex, combined);
        string diskStatus = MatchValue(DiskStatusRegex, combined);
        string sessionStatus = MatchValue(SessionStatusRegex, combined);
        string driverFlags = MatchValue(DriverFlagsRegex, combined);
        string supportedModes = MatchValue(SupportedModesRegex, combined);

        bool looksLikeCd = mediaClass.Contains("CD", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("CD", StringComparison.OrdinalIgnoreCase);
        bool looksLikeDvd = mediaClass.Contains("DVD", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("DVD", StringComparison.OrdinalIgnoreCase);
        bool looksLikeBd = mediaClass.Contains("BD", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("BD", StringComparison.OrdinalIgnoreCase);

        if (requireBluRay)
        {
            if (!looksLikeBd)
            {
                string detected = string.Join(" / ", new[] { mediaClass, mediaType }.Where(v => !string.IsNullOrWhiteSpace(v)));
                if (string.IsNullOrWhiteSpace(detected)) detected = "tipo desconocido";
                throw new InvalidOperationException(
                    $"El medio insertado no se reconoce como Blu-ray grabable BD-R/BD-RE ({detected}).");
            }
        }
        else if (allowDvdData)
        {
            if ((!looksLikeCd && !looksLikeDvd) || looksLikeBd)
            {
                string detected = string.Join(" / ", new[] { mediaClass, mediaType }.Where(v => !string.IsNullOrWhiteSpace(v)));
                if (string.IsNullOrWhiteSpace(detected)) detected = "tipo desconocido";
                throw new InvalidOperationException(
                    $"El medio insertado no se reconoce como CD o DVD grabable compatible ({detected}).");
            }
        }
        else if (looksLikeDvd || looksLikeBd || !looksLikeCd)
        {
            string detected = string.Join(" / ", new[] { mediaClass, mediaType }.Where(v => !string.IsNullOrWhiteSpace(v)));
            if (string.IsNullOrWhiteSpace(detected)) detected = "tipo desconocido";
            throw new InvalidOperationException(
                $"El medio insertado no se reconoce como CD grabable ({detected}). Audio CD y CD Extra requieren CD-R o CD-RW.");
        }

        string mediumName = looksLikeBd ? "Blu-ray" : looksLikeDvd ? "DVD" : "CD";
        if (string.IsNullOrWhiteSpace(diskStatus))
        {
            throw new InvalidOperationException(
                $"cdrtools no pudo determinar el estado del {mediumName}. Expulsa y vuelve a insertar el disco, espera unos segundos y prueba otra vez. " +
                "Si continúa, ejecuta dvdrtool como administrador y revisa el Registro.");
        }

        if (!diskStatus.Contains("empty", StringComparison.OrdinalIgnoreCase))
        {
            bool erasable = combined.Contains("Disk Is erasable", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("RW", StringComparison.OrdinalIgnoreCase);
            string advice = erasable
                ? $"El {mediumName} regrabable contiene datos. Bórralo primero con cdrecord y vuelve a intentar."
                : $"El {mediumName} ya fue usado o quedó con una sesión de un intento anterior. Inserta un medio virgen.";
            throw new InvalidOperationException(
                $"El {mediumName} no está vacío (estado: {diskStatus}). Para iniciar una grabación nueva se necesita un medio vacío.\n\n{advice}");
        }

        bool supportsTao = string.IsNullOrWhiteSpace(supportedModes)
            || supportedModes.Contains("TAO", StringComparison.OrdinalIgnoreCase);
        if (!allowDvdData && !requireBluRay && !supportsTao)
        {
            throw new InvalidOperationException(
                $"La grabadora no anuncia soporte TAO (modos: {supportedModes}). La versión estable usa TAO para Audio CD/CD Extra.");
        }

        bool burnFree = driverFlags.Contains("BURNFREE", StringComparison.OrdinalIgnoreCase)
            || drive.AllOutput.Contains("burnfree", StringComparison.OrdinalIgnoreCase);
        bool forceSpeed = driverFlags.Contains("FORCESPEED", StringComparison.OrdinalIgnoreCase);

        log?.Report($"Preflight OK: medio={mediaType}, estado={diskStatus}, sesión={sessionStatus}, tipo={(looksLikeBd ? "Blu-ray" : looksLikeDvd ? "DVD" : "CD")}, TAO={(supportsTao ? "sí" : "no")}, BURN-Free={(burnFree ? "sí" : "no")}, FORCESPEED={(forceSpeed ? "sí" : "no")}.");
        return new MediaPreflight(mediaClass, mediaType, diskStatus, sessionStatus, supportsTao, burnFree, forceSpeed);
    }

    private static string MatchValue(Regex regex, string text)
    {
        Match match = regex.Match(text);
        return match.Success ? match.Groups["value"].Value.Trim() : "";
    }

    private static bool HasCdText(string? albumTitle, string? albumArtist) =>
        !string.IsNullOrWhiteSpace(albumTitle) || !string.IsNullOrWhiteSpace(albumArtist);

    private static string CleanCdTextValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();

        // CD-TEXT clásico usa un juego de caracteres limitado. Latin-1 conserva
        // correctamente los caracteres occidentales habituales (incluidos acentos).
        return Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(cleaned));
    }

    private static IReadOnlyList<string> PrepareCdTextInfoFiles(
        IReadOnlyList<string> preparedWavFiles,
        IReadOnlyList<string> originalAudioFiles,
        string tempAudioDirectory,
        string? albumTitle,
        string? albumArtist,
        IProgress<string>? log)
    {
        if (!HasCdText(albumTitle, albumArtist))
            return preparedWavFiles;

        Directory.CreateDirectory(tempAudioDirectory);

        string title = CleanCdTextValue(albumTitle);
        string artist = CleanCdTextValue(albumArtist);
        var result = new List<string>(preparedWavFiles.Count);

        for (int i = 0; i < preparedWavFiles.Count; i++)
        {
            string sourceWav = preparedWavFiles[i];
            string targetWav = Path.Combine(tempAudioDirectory, $"track_{i + 1:00}.wav");

            if (!Path.GetFullPath(sourceWav).Equals(Path.GetFullPath(targetWav), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceWav, targetWav, overwrite: true);

            string originalName = i < originalAudioFiles.Count
                ? Path.GetFileNameWithoutExtension(originalAudioFiles[i])
                : $"Pista {i + 1:00}";
            string trackTitle = CleanCdTextValue(originalName);

            WavHelper.GetCddaTrackLength(targetWav, out long fullSectors, out int remainingSamples);

            var inf = new List<string>
            {
                $"Albumtitle='{title}'",
                $"Albumperformer='{artist}'",
                $"Tracktitle='{trackTitle}'",
                $"Performer='{artist}'",
                $"Tracklength={fullSectors}, {remainingSamples}"
            };

            string infPath = Path.ChangeExtension(targetWav, ".inf");
            File.WriteAllLines(infPath, inf, Encoding.Latin1);
            result.Add(targetWav);
        }

        log?.Report(
            $"CD-TEXT preparado: título='{title}', artista='{artist}'. " +
            "Los títulos de pista se tomaron de los nombres de archivo.");
        return result;
    }

    private static List<string> BuildStableCdrecordArgs(
        string deviceSpec,
        MediaPreflight preflight,
        int? writeSpeed = null)
    {
        var args = new List<string>
        {
            "-v",
            $"dev={deviceSpec}",
            "fs=32m"
        };

        // null = automática.
        // Una velocidad manual envía speed=N. No usamos forcespeed de forma
        // automática: puede empeorar la calidad de escritura y no garantiza
        // velocidades que la combinación grabadora/medio no admita.
        if (writeSpeed is > 0)
            args.Add($"speed={writeSpeed.Value}");

        var driverOptions = new List<string>();
        if (preflight.SupportsBurnFree)
            driverOptions.Add("burnfree");

        // Asegura que una ejecución anterior no deje activo forcespeed.
        if (preflight.SupportsForceSpeed)
            driverOptions.Add("noforcespeed");

        if (driverOptions.Count > 0)
            args.Add($"driveropts={string.Join(",", driverOptions)}");

        return args;
    }

    public async Task BurnAsync(
        string toolsDirectory,
        string deviceSpec,
        IReadOnlyList<string> audioFiles,
        string dataFolder,
        string volumeLabel,
        string? ffmpegPath,
        string? albumArtist = null,
        string? albumTitle = null,
        int? writeSpeed = null,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        IProgress<DiscCapacityInfo>? capacityProgress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        if (string.IsNullOrWhiteSpace(deviceSpec)) throw new InvalidOperationException("Selecciona un dispositivo cdrtools.");
        if (audioFiles.Count == 0) throw new InvalidOperationException("Agrega al menos una pista de audio.");
        if (!Directory.Exists(dataFolder)) throw new DirectoryNotFoundException("La carpeta de datos no existe.");

        volumeLabel = SanitizeVolumeLabel(volumeLabel);
        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");
        string mkisofs = Path.Combine(toolsDirectory, "mkisofs.exe");
        string tempIso = Path.Combine(Path.GetTempPath(), $"dvdrtool_{Guid.NewGuid():N}.iso");
        string tempProbeIso = Path.Combine(Path.GetTempPath(), $"dvdrtool_Probe_{Guid.NewGuid():N}.iso");
        string tempAudioDir = Path.Combine(Path.GetTempPath(), $"dvdrtool_Audio_{Guid.NewGuid():N}");

        try
        {
            // Paso 0: no se convierte ni se escribe nada hasta confirmar que la grabadora
            // responde, que el medio es un CD y que está realmente vacío.
            progress?.Report((0.01, "Comprobación previa de grabadora y CD…"));
            MediaPreflight preflight = await RunMediaPreflightAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken);

            progress?.Report((0.06, "Preparando pistas para CD Audio…"));
            var convertProgress = new Progress<(double progress, string status)>(p =>
                progress?.Report((0.06 + (p.progress * 0.12), p.status)));
            IReadOnlyList<string> wavFiles = await _audioConverter.PrepareForCddaAsync(
                audioFiles, toolsDirectory, ffmpegPath, tempAudioDir, convertProgress, log, cancellationToken);

            bool useCdText = HasCdText(albumTitle, albumArtist);
            if (useCdText)
            {
                wavFiles = PrepareCdTextInfoFiles(
                    wavFiles, audioFiles, tempAudioDir, albumTitle, albumArtist, log);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Preflight exacto de capacidad después de convertir el audio. Así la aplicación
            // jamás empieza la primera sesión si el proyecto no cabe físicamente.
            progress?.Report((0.19, "Comprobando capacidad exacta del CD y del proyecto…"));
            long preparedAudioSectors = wavFiles.Sum(WavHelper.GetCddaSectorCount);
            DiscCapacityInfo capacity = await AnalyzePreparedProjectAsync(
                cdrecord, mkisofs, toolsDirectory, deviceSpec, preparedAudioSectors, dataFolder, volumeLabel,
                tempProbeIso, log, cancellationToken);
            capacityProgress?.Report(capacity);
            EnsureProjectFits(capacity);

            // CD Extra: la primera sesión es audio multisesión.
            // Cuando hay CD-TEXT, cdrecord debe escribir la información en el lead-in;
            // usamos DAO/SAO para esta sesión, igual que los ejemplos documentados de
            // cdrtools/cdrtfe. Sin CD-TEXT conservamos TAO para el flujo normal.
            progress?.Report((0.24, "Grabando primera sesión: CD Audio…"));
            var audioArgs = BuildStableCdrecordArgs(deviceSpec, preflight, writeSpeed);
            audioArgs.Add("-multi");
            audioArgs.Add(useCdText ? "-dao" : "-tao");
            audioArgs.Add("-pad");
            if (useCdText)
            {
                audioArgs.Add("-useinfo");
                audioArgs.Add("-text");
                log?.Report("CD-TEXT activo: primera sesión de CD Extra se grabará en DAO/SAO con -useinfo -text.");
            }
            audioArgs.Add("-audio");
            audioArgs.AddRange(wavFiles);

            await RunToolAsync(cdrecord, audioArgs, toolsDirectory, log, cancellationToken, exactWriteSpeed: writeSpeed);

            // Algunas unidades tardan un instante en publicar el nuevo NWA. Se reintenta
            // -msinfo sin tocar el disco antes de declarar un fallo.
            progress?.Report((0.57, "Leyendo posición de la segunda sesión…"));
            Match? ms = null;
            string msOutput = "";
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CommandResult msResult = await RunToolAsync(
                    cdrecord,
                    new[] { "-msinfo", $"dev={deviceSpec}" },
                    toolsDirectory,
                    log,
                    cancellationToken,
                    allowNonZeroExitCode: true);

                msOutput = msResult.AllOutput;
                Match candidate = MsInfoRegex.Match(msOutput);
                if (candidate.Success)
                {
                    ms = candidate;
                    break;
                }

                if (attempt < 4)
                {
                    log?.Report($"-msinfo todavía no está disponible (intento {attempt}/4); esperando a la grabadora…");
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                }
            }

            if (ms is null || !ms.Success)
                throw new InvalidOperationException(
                    "La sesión de audio se escribió, pero cdrecord no pudo obtener -msinfo para iniciar la sesión de datos. " +
                    "No vuelvas a usar este CD-R para empezar de cero; revisa el Registro. Salida final: " +
                    LastMeaningfulLines(msOutput, 8));

            string msInfo = $"{ms.Groups["first"].Value},{ms.Groups["next"].Value}";
            log?.Report($"CD Extra: posición confirmada de la segunda sesión: -C {msInfo}.");

            // Para CD Extra, -C se usa SIN -M: la primera sesión contiene audio, no un ISO previo.
            progress?.Report((0.66, "Creando sesión ISO9660/Joliet de datos…"));
            var isoArgs = new List<string>
            {
                "-R",
                "-J",
                "-iso-level", "3",
                "-V", volumeLabel,
                "-C", msInfo,
                "-o", tempIso,
                Path.GetFullPath(dataFolder)
            };

            await RunToolAsync(mkisofs, isoArgs, toolsDirectory, log, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.82, "Grabando segunda sesión de datos y cerrando el CD…"));

            var dataArgs = BuildStableCdrecordArgs(deviceSpec, preflight, writeSpeed);
            dataArgs.Add("-tao");
            dataArgs.Add("-eject");
            dataArgs.Add("-pad");
            dataArgs.Add("-data");
            dataArgs.Add(tempIso);
            await RunToolAsync(cdrecord, dataArgs, toolsDirectory, log, cancellationToken, exactWriteSpeed: writeSpeed);

            progress?.Report((1.0, "CD Extra finalizado correctamente. Audio + datos grabados en dos sesiones."));
        }
        finally
        {
            try
            {
                if (File.Exists(tempIso)) File.Delete(tempIso);
                if (File.Exists(tempProbeIso)) File.Delete(tempProbeIso);
                if (Directory.Exists(tempAudioDir)) Directory.Delete(tempAudioDir, recursive: true);
            }
            catch
            {
                // Un archivo temporal bloqueado no debe ocultar el resultado principal.
            }
        }
    }

    /// <summary>
    /// Graba un CD Audio tradicional, sin abrir una segunda sesión de datos.
    /// Reutiliza el mismo preflight y la misma conversión estable que usa CD Extra.
    /// </summary>
    public async Task BurnAudioOnlyAsync(
        string toolsDirectory,
        string deviceSpec,
        IReadOnlyList<string> audioFiles,
        string? ffmpegPath,
        string? albumArtist = null,
        string? albumTitle = null,
        int? writeSpeed = null,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        if (string.IsNullOrWhiteSpace(deviceSpec)) throw new InvalidOperationException("Selecciona un dispositivo cdrtools.");
        if (audioFiles.Count == 0) throw new InvalidOperationException("Agrega al menos una pista de audio.");

        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");
        string tempAudioDir = Path.Combine(Path.GetTempPath(), $"dvdrtool_AudioOnly_{Guid.NewGuid():N}");

        try
        {
            progress?.Report((0.02, "Comprobando grabadora y CD virgen…"));
            MediaPreflight preflight = await RunMediaPreflightAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken);

            progress?.Report((0.10, "Preparando pistas para Audio CD…"));
            var convertProgress = new Progress<(double progress, string status)>(p =>
                progress?.Report((0.10 + p.progress * 0.28, p.status)));
            IReadOnlyList<string> wavFiles = await _audioConverter.PrepareForCddaAsync(
                audioFiles, toolsDirectory, ffmpegPath, tempAudioDir, convertProgress, log, cancellationToken);

            bool useCdText = HasCdText(albumTitle, albumArtist);
            if (useCdText)
            {
                wavFiles = PrepareCdTextInfoFiles(
                    wavFiles, audioFiles, tempAudioDir, albumTitle, albumArtist, log);
            }

            long audioSectors = wavFiles.Sum(WavHelper.GetCddaSectorCount);
            progress?.Report((0.40, "Comprobando capacidad del CD…"));
            long mediaSectors = await ReadMediaCapacitySectorsAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken);

            if (mediaSectors > 0 && audioSectors + SafetyMarginSectors > mediaSectors)
            {
                long excess = audioSectors + SafetyMarginSectors - mediaSectors;
                throw new InvalidOperationException(
                    "Las pistas no caben en el CD insertado. La grabación se detuvo antes de escribir.\n\n" +
                    $"Audio: {DiscCapacityInfo.FormatCdTime(audioSectors)}\n" +
                    $"Capacidad: {DiscCapacityInfo.FormatCdTime(mediaSectors)}\n" +
                    $"Exceso aproximado: {DiscCapacityInfo.FormatCdTime(excess)}");
            }

            progress?.Report((0.48, "Grabando Audio CD…"));
            var args = BuildStableCdrecordArgs(deviceSpec, preflight, writeSpeed);
            args.Add(useCdText ? "-dao" : "-tao");
            args.Add("-pad");
            args.Add("-eject");
            if (useCdText)
            {
                args.Add("-useinfo");
                args.Add("-text");
                log?.Report("CD-TEXT activo: Audio CD se grabará en DAO/SAO con -useinfo -text.");
            }
            args.Add("-audio");
            args.AddRange(wavFiles);

            await RunToolAsync(cdrecord, args, toolsDirectory, log, cancellationToken, exactWriteSpeed: writeSpeed);
            progress?.Report((1.0, "Audio CD grabado correctamente."));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempAudioDir)) Directory.Delete(tempAudioDir, recursive: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// Graba un CD de datos ISO9660/Joliet, sin crear pistas CD Audio.
    /// </summary>
    public async Task BurnDataOnlyAsync(
        string toolsDirectory,
        string deviceSpec,
        string dataFolder,
        string volumeLabel,
        int? writeSpeed = null,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        if (string.IsNullOrWhiteSpace(deviceSpec)) throw new InvalidOperationException("Selecciona un dispositivo cdrtools.");
        if (!Directory.Exists(dataFolder)) throw new DirectoryNotFoundException("La carpeta de datos no existe.");
        if (!Directory.EnumerateFileSystemEntries(dataFolder, "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Agrega al menos un archivo o una carpeta al proyecto de datos.");

        volumeLabel = SanitizeVolumeLabel(volumeLabel);
        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");
        string mkisofs = Path.Combine(toolsDirectory, "mkisofs.exe");
        string tempIso = Path.Combine(Path.GetTempPath(), $"dvdrtool_DataOnly_{Guid.NewGuid():N}.iso");

        try
        {
            progress?.Report((0.03, "Comprobando grabadora y medio CD/DVD virgen…"));
            MediaPreflight preflight = await RunMediaPreflightAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken, allowDvdData: true);

            bool isDvd = preflight.MediaClass.Contains("DVD", StringComparison.OrdinalIgnoreCase)
                || preflight.MediaType.Contains("DVD", StringComparison.OrdinalIgnoreCase);
            string mediumName = isDvd ? "DVD" : "CD";

            progress?.Report((0.18, "Creando imagen ISO9660/Joliet…"));
            await RunToolAsync(
                mkisofs,
                new[]
                {
                    "-J", "-R", "-iso-level", "3",
                    "-V", volumeLabel,
                    "-o", tempIso,
                    Path.GetFullPath(dataFolder)
                },
                toolsDirectory,
                log,
                cancellationToken);

            long dataSectors = (new FileInfo(tempIso).Length + 2047L) / 2048L;
            progress?.Report((0.42, $"Comprobando capacidad del {mediumName}…"));
            long mediaSectors = await ReadMediaCapacitySectorsAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken);

            if (mediaSectors > 0 && dataSectors + SafetyMarginSectors > mediaSectors)
            {
                long excess = dataSectors + SafetyMarginSectors - mediaSectors;
                throw new InvalidOperationException(
                    "Los datos no caben en el medio insertado. La grabación se detuvo antes de escribir.\n\n" +
                    $"Datos: {DiscCapacityInfo.FormatDataMiB(dataSectors)}\n" +
                    $"Capacidad: {DiscCapacityInfo.FormatDataMiB(mediaSectors)}\n" +
                    $"Exceso aproximado: {DiscCapacityInfo.FormatDataMiB(excess)}");
            }

            progress?.Report((0.52, $"Grabando {mediumName} de datos…"));
            var args = BuildStableCdrecordArgs(deviceSpec, preflight, writeSpeed);
            // DVD no dispone de TAO. Para una grabación de datos de una sola sesión
            // usamos DAO/SAO y finalizamos el disco para máxima compatibilidad.
            if (isDvd)
                args.Add("-dao");
            else
            {
                args.Add("-tao");
                args.Add("-pad");
            }
            args.Add("-eject");
            args.Add("-data");
            args.Add(tempIso);

            await RunToolAsync(cdrecord, args, toolsDirectory, log, cancellationToken, exactWriteSpeed: writeSpeed);
            progress?.Report((1.0, $"{mediumName} de datos grabado correctamente."));
        }
        finally
        {
            TryDelete(tempIso);
        }
    }

    /// <summary>
    /// Graba directamente una imagen Blu-ray Video ISO ya preparada. No crea ni
    /// modifica la autoría BDMV; la imagen se escribe sector a sector como datos.
    /// </summary>
    public async Task BurnBluRayIsoAsync(
        string toolsDirectory,
        string deviceSpec,
        string isoPath,
        int? writeSpeed = null,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        if (string.IsNullOrWhiteSpace(deviceSpec)) throw new InvalidOperationException("Selecciona un dispositivo cdrtools.");
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            throw new FileNotFoundException("No se encontró la imagen Blu-ray ISO seleccionada.", isoPath);
        if (!Path.GetExtension(isoPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La pestaña Blu-ray Video requiere una imagen .ISO ya preparada.");

        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");
        progress?.Report((0.05, "Comprobando grabadora y Blu-ray virgen…"));
        MediaPreflight preflight = await RunMediaPreflightAsync(
            cdrecord, toolsDirectory, deviceSpec, log, cancellationToken, requireBluRay: true);

        long isoBytes = new FileInfo(isoPath).Length;
        long isoSectors = (isoBytes + 2047L) / 2048L;

        progress?.Report((0.22, "Comprobando capacidad del Blu-ray…"));
        long mediaSectors = await ReadLargeMediaCapacitySectorsAsync(
            cdrecord, toolsDirectory, deviceSpec, log, cancellationToken);

        if (mediaSectors > 0 && isoSectors + SafetyMarginSectors > mediaSectors)
        {
            long excessSectors = isoSectors + SafetyMarginSectors - mediaSectors;
            throw new InvalidOperationException(
                "La imagen Blu-ray no cabe en el medio insertado. La grabación se detuvo antes de escribir.\n\n" +
                $"Imagen: {isoBytes / 1024.0 / 1024 / 1024:0.00} GB\n" +
                $"Capacidad: {mediaSectors * 2048.0 / 1024 / 1024 / 1024:0.00} GB\n" +
                $"Exceso aproximado: {excessSectors * 2048.0 / 1024 / 1024 / 1024:0.00} GB");
        }

        progress?.Report((0.35, "Grabando imagen Blu-ray Video…"));
        var args = BuildStableCdrecordArgs(deviceSpec, preflight, writeSpeed);
        // Los medios Blu-ray no usan TAO. DAO/SAO deja una única sesión cerrada,
        // adecuada para escribir una imagen Blu-ray ya autorizada.
        args.Add("-dao");
        args.Add("-eject");
        args.Add("-data");
        args.Add(Path.GetFullPath(isoPath));

        await RunToolAsync(cdrecord, args, toolsDirectory, log, cancellationToken, exactWriteSpeed: writeSpeed);
        progress?.Report((1.0, "Blu-ray grabado correctamente."));
    }

    /// <summary>
    /// Graba una estructura Blu-ray ya autorizada desde carpeta. La estructura se
    /// convierte primero a una imagen UDF 2.50 temporal usando IMAPI2FS de Windows.
    /// </summary>
    public async Task BurnBluRayFolderAsync(
        string toolsDirectory,
        string deviceSpec,
        string sourceFolder,
        string volumeLabel = "BLURAY_VIDEO",
        int? writeSpeed = null,
        IProgress<(double progress, string status)>? progress = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTools(toolsDirectory);
        if (string.IsNullOrWhiteSpace(deviceSpec))
            throw new InvalidOperationException("Selecciona un dispositivo cdrtools.");

        string root = BluRayImageBuilder.NormalizeBluRayRoot(sourceFolder);
        string cdrecord = Path.Combine(toolsDirectory, "cdrecord.exe");
        string tempIso = Path.Combine(Path.GetTempPath(), $"dvdrtool_Bluray_{Guid.NewGuid():N}.iso");

        try
        {
            progress?.Report((0.03, "Comprobando grabadora y Blu-ray virgen…"));
            MediaPreflight preflight = await RunMediaPreflightAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken, requireBluRay: true);

            progress?.Report((0.10, "Consultando capacidad del Blu-ray…"));
            long mediaSectors = await ReadLargeMediaCapacitySectorsAsync(
                cdrecord, toolsDirectory, deviceSpec, log, cancellationToken);

            long sourceBytes = BluRayImageBuilder.CalculateDirectoryBytes(root);
            if (mediaSectors > 0)
            {
                long conservativeSectors = (sourceBytes + 2047L) / 2048L + SafetyMarginSectors;
                if (conservativeSectors > mediaSectors)
                {
                    throw new InvalidOperationException(
                        "La carpeta Blu-ray no cabe en el medio insertado. " +
                        "La grabación se detuvo antes de crear la imagen temporal.");
                }
            }

            log?.Report($"Blu-ray carpeta: creando UDF 2.50 desde {root}");
            long isoBytes = await BluRayImageBuilder.CreateUdf250ImageAsync(
                root,
                tempIso,
                volumeLabel,
                new Progress<(double progress, string status)>(p =>
                {
                    double mapped = 0.12 + p.progress * 0.55;
                    progress?.Report((Math.Min(0.70, mapped), p.status));
                }),
                cancellationToken);

            long isoSectors = (isoBytes + 2047L) / 2048L;
            if (mediaSectors > 0 && isoSectors + SafetyMarginSectors > mediaSectors)
            {
                throw new InvalidOperationException(
                    "La imagen UDF 2.50 generada no cabe en el Blu-ray insertado.\n\n" +
                    $"Imagen: {isoBytes / 1024.0 / 1024 / 1024:0.00} GB\n" +
                    $"Capacidad: {mediaSectors * 2048.0 / 1024 / 1024 / 1024:0.00} GB");
            }

            progress?.Report((0.72, "Grabando estructura Blu-ray UDF 2.50…"));
            var args = BuildStableCdrecordArgs(deviceSpec, preflight, writeSpeed);
            args.Add("-dao");
            args.Add("-eject");
            args.Add("-data");
            args.Add(tempIso);

            await RunToolAsync(
                cdrecord, args, toolsDirectory, log, cancellationToken, exactWriteSpeed: writeSpeed);

            progress?.Report((1.0, "Blu-ray grabado correctamente."));
        }
        finally
        {
            TryDelete(tempIso);
        }
    }

    private static async Task<long> ReadLargeMediaCapacitySectorsAsync(
        string cdrecord,
        string toolsDirectory,
        string deviceSpec,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        CommandResult media = await RunToolAsync(
            cdrecord,
            new[] { "-minfo", "-silent", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);

        long sectors = ParseLargeMediaCapacitySectors(media.AllOutput);
        if (sectors > 0) return sectors;

        CommandResult prcap = await RunToolAsync(
            cdrecord,
            new[] { "-prcap", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);
        return ParseLargeMediaCapacitySectors(prcap.AllOutput);
    }

    private static long ParseLargeMediaCapacitySectors(string output)
    {
        Match total = BlocksTotalRegex.Match(output);
        if (total.Success
            && long.TryParse(total.Groups["value"].Value, out long totalValue)
            && IsPlausibleLargeMediaCapacity(totalValue))
            return totalValue;

        Match diskCapacity = DiskCapacityBlocksRegex.Match(output);
        if (diskCapacity.Success
            && long.TryParse(diskCapacity.Groups["value"].Value, out long diskCapacityValue)
            && IsPlausibleLargeMediaCapacity(diskCapacityValue))
            return diskCapacityValue;

        Match end = EndLbaRegex.Match(output);
        if (end.Success
            && long.TryParse(end.Groups["value"].Value, out long endLba)
            && IsPlausibleLargeMediaCapacity(endLba + 1))
            return endLba + 1;

        return 0;
    }

    private static bool IsPlausibleLargeMediaCapacity(long sectors) =>
        sectors >= 1_000_000 && sectors <= 80_000_000;

    private static async Task<long> ReadMediaCapacitySectorsAsync(
        string cdrecord,
        string toolsDirectory,
        string deviceSpec,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        CommandResult media = await RunToolAsync(
            cdrecord,
            new[] { "-minfo", "-silent", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);

        long sectors = ParseMediaCapacitySectors(media.AllOutput);
        if (sectors > 0) return sectors;

        CommandResult atip = await RunToolAsync(
            cdrecord,
            new[] { "-atip", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);
        sectors = ParseMediaCapacitySectors(atip.AllOutput);
        if (sectors > 0) return sectors;

        CommandResult prcap = await RunToolAsync(
            cdrecord,
            new[] { "-prcap", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);
        return ParsePrCapSectors(prcap.AllOutput);
    }

    private static async Task<DiscCapacityInfo> AnalyzePreparedProjectAsync(
        string cdrecord,
        string mkisofs,
        string toolsDirectory,
        string deviceSpec,
        long audioSectors,
        string dataFolder,
        string volumeLabel,
        string probeIso,
        IProgress<string>? log,
        CancellationToken cancellationToken,
        bool audioEstimateComplete = true,
        int unknownAudioCount = 0,
        string? unknownAudioNames = null)
    {
        // Consulta el medio sin comenzar una escritura. Las distintas compilaciones
        // de cdrtools/cdrtfe no siempre muestran "Blocks total" con -minfo, por eso
        // también aceptamos ATIP lead-out y, como tercer respaldo, End LBA de -prcap.
        // Esto evita que un CD-R válido aparezca como "capacidad desconocida".
        CommandResult media = await RunToolAsync(
            cdrecord,
            new[] { "-minfo", "-silent", "-v", $"dev={deviceSpec}" },
            toolsDirectory,
            log,
            cancellationToken,
            allowNonZeroExitCode: true);

        long mediaSectors = ParseMediaCapacitySectors(media.AllOutput);

        if (mediaSectors <= 0)
        {
            log?.Report("-minfo no devolvió la capacidad; consultando ATIP del CD…");
            CommandResult atip = await RunToolAsync(
                cdrecord,
                new[] { "-atip", "-v", $"dev={deviceSpec}" },
                toolsDirectory,
                log,
                cancellationToken,
                allowNonZeroExitCode: true);

            mediaSectors = ParseMediaCapacitySectors(atip.AllOutput);
        }

        if (mediaSectors <= 0)
        {
            log?.Report("ATIP no devolvió la capacidad; consultando -prcap como respaldo…");
            CommandResult prcap = await RunToolAsync(
                cdrecord,
                new[] { "-prcap", "-v", $"dev={deviceSpec}" },
                toolsDirectory,
                log,
                cancellationToken,
                allowNonZeroExitCode: true);

            mediaSectors = ParsePrCapSectors(prcap.AllOutput);
        }

        // Imagen de prueba: para CD Extra la primera sesión es audio, por eso no se usa -M.
        await RunToolAsync(
            mkisofs,
            new[]
            {
                "-J", "-R", "-iso-level", "3",
                "-V", volumeLabel,
                "-o", probeIso,
                Path.GetFullPath(dataFolder)
            },
            toolsDirectory,
            log,
            cancellationToken);

        long dataSectors = (new FileInfo(probeIso).Length + 2047L) / 2048L;
        var info = new DiscCapacityInfo
        {
            MediaSectors = mediaSectors,
            AudioSectors = audioSectors,
            DataSectors = dataSectors,
            OverheadSectors = FirstAdditionalSessionOverheadSectors,
            SafetySectors = SafetyMarginSectors,
            AudioEstimateComplete = audioEstimateComplete,
            UnknownAudioCount = unknownAudioCount,
            UnknownAudioNames = unknownAudioNames
        };

        log?.Report(
            $"Capacidad: audio={info.AudioSectors} sectores ({DiscCapacityInfo.FormatCdTime(info.AudioSectors)}), " +
            $"datos={info.DataSectors} sectores ({DiscCapacityInfo.FormatDataMiB(info.DataSectors)}), " +
            $"multisesión={info.OverheadSectors}, margen={info.SafetySectors}, total={info.RequiredSectors}.");

        if (!info.AudioEstimateComplete)
            log?.Report($"Capacidad parcial: faltan {info.UnknownAudioCount} pista(s) por medir. La comprobación exacta se hará después de convertir y antes de escribir el CD.");

        if (info.IsKnown)
        {
            string capacityMessage;
            if (!info.AudioEstimateComplete && !info.DefinitelyExceeds)
                capacityMessage = $"La parte conocida deja {DiscCapacityInfo.FormatCdTime(info.RemainingSectors)}, pero faltan duraciones por medir.";
            else if (info.Fits)
                capacityMessage = $"Quedan aproximadamente {DiscCapacityInfo.FormatCdTime(info.RemainingSectors)}.";
            else
                capacityMessage = $"EXCEDE por aproximadamente {DiscCapacityInfo.FormatCdTime(-info.RemainingSectors)}.";

            log?.Report(
                $"Medio: {info.MediaSectors} sectores, {DiscCapacityInfo.FormatCdTime(info.MediaSectors)} de tiempo CD, " +
                $"{DiscCapacityInfo.FormatDataMiB(info.MediaSectors)} equivalentes de datos. {capacityMessage}");
        }
        else
        {
            log?.Report("No se pudo obtener una capacidad fiable del medio; cdrecord hará la comprobación final antes de escribir.");
        }

        return info;
    }

    private static void EnsureProjectFits(DiscCapacityInfo info)
    {
        if (!info.IsKnown || info.Fits) return;

        long excess = -info.RemainingSectors;
        throw new InvalidOperationException(
            "El proyecto no cabe en el CD insertado. La grabación se ha detenido antes de escribir.\n\n" +
            $"Audio: {DiscCapacityInfo.FormatCdTime(info.AudioSectors)}\n" +
            $"Datos: {DiscCapacityInfo.FormatDataMiB(info.DataSectors)}\n" +
            $"Reserva multisesión: {DiscCapacityInfo.FormatCdTime(info.OverheadSectors)}\n" +
            $"Margen de seguridad: {DiscCapacityInfo.FormatCdTime(info.SafetySectors)}\n" +
            $"Proyecto total: {DiscCapacityInfo.FormatCdTime(info.RequiredSectors)}\n" +
            $"Capacidad del medio: {DiscCapacityInfo.FormatCdTime(info.MediaSectors)} ({DiscCapacityInfo.FormatDataMiB(info.MediaSectors)} eq.)\n" +
            $"Exceso: {DiscCapacityInfo.FormatCdTime(excess)} ({DiscCapacityInfo.FormatDataMiB(excess)} eq.)\n\n" +
            "Quita algunas pistas o reduce la carpeta de datos. En un CD-R, 'Disk Is not erasable' es normal; " +
            "el dato importante cuando no cabe es un valor negativo en 'Blocks remaining'.");
    }

    private static long ParseMediaCapacitySectors(string output)
    {
        // "Blocks total" representa la capacidad física total. No usamos
        // "Blocks remaining" como capacidad porque en discos multisesión indica
        // lo que queda después de las sesiones existentes.
        Match total = BlocksTotalRegex.Match(output);
        if (total.Success
            && long.TryParse(total.Groups["value"].Value, out long totalValue)
            && IsPlausibleCdCapacity(totalValue))
            return totalValue;

        // En CD-R/CD-RW cdrtools suele publicar la capacidad mediante el inicio
        // del lead-out ATIP (p.ej. 359849 ~= 80 minutos).
        Match atip = AtipLeadOutRegex.Match(output);
        if (atip.Success
            && long.TryParse(atip.Groups["value"].Value, out long atipValue)
            && IsPlausibleCdCapacity(atipValue))
            return atipValue;

        Match atipMsf = AtipLeadOutMsfRegex.Match(output);
        if (atipMsf.Success
            && int.TryParse(atipMsf.Groups["min"].Value, out int minutes)
            && int.TryParse(atipMsf.Groups["sec"].Value, out int seconds)
            && int.TryParse(atipMsf.Groups["frame"].Value, out int frames)
            && seconds is >= 0 and < 60
            && frames is >= 0 and < 75)
        {
            long msfSectors = ((long)minutes * 60L + seconds) * 75L + frames - 150L;
            if (IsPlausibleCdCapacity(msfSectors)) return msfSectors;
        }

        Match diskCapacity = DiskCapacityBlocksRegex.Match(output);
        if (diskCapacity.Success
            && long.TryParse(diskCapacity.Groups["value"].Value, out long diskCapacityValue)
            && IsPlausibleCdCapacity(diskCapacityValue))
            return diskCapacityValue;

        Match last = LastLeadOutRegex.Match(output);
        if (last.Success
            && long.TryParse(last.Groups["value"].Value, out long lastValue)
            && IsPlausibleCdCapacity(lastValue))
            return lastValue;

        return 0;
    }

    private static long ParsePrCapSectors(string output)
    {
        Match diskCapacity = DiskCapacityBlocksRegex.Match(output);
        if (diskCapacity.Success
            && long.TryParse(diskCapacity.Groups["value"].Value, out long diskCapacityValue)
            && IsPlausibleCdCapacity(diskCapacityValue))
            return diskCapacityValue;

        Match end = EndLbaRegex.Match(output);
        if (end.Success
            && long.TryParse(end.Groups["value"].Value, out long endLba)
            && IsPlausibleCdCapacity(endLba + 1))
            return endLba + 1;

        return 0;
    }

    private static bool IsPlausibleCdCapacity(long sectors)
    {
        // Incluye CD-R de 63/74/80/90/99 min y deja margen para medios poco comunes,
        // pero rechaza números absurdos que algunas unidades imprimen como
        // "No Media Present or Unknown Capacity".
        return sectors >= 250_000 && sectors <= 600_000;
    }

    private static string FormatCdTime(long sectors) => DiscCapacityInfo.FormatCdTime(sectors);

    private static async Task<CommandResult> RunToolAsync(
        string executable,
        IEnumerable<string> arguments,
        string toolsDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken,
        bool allowNonZeroExitCode = false,
        int? exactWriteSpeed = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = toolsDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var argsList = arguments.ToList();
        foreach (string arg in argsList) psi.ArgumentList.Add(arg);
        ConfigureCygwinPath(psi, toolsDirectory);

        log?.Report($"> {Path.GetFileName(executable)} {FormatArguments(argsList)}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        string? speedMismatchMessage = null;
        int speedAbortRequested = 0;

        void CheckSelectedWriteSpeed(string line)
        {
            if (exactWriteSpeed is not > 0 || Volatile.Read(ref speedAbortRequested) != 0)
                return;

            Match match = ActualWriteSpeedRegex.Match(line);
            if (!match.Success)
                return;

            string value = match.Groups["speed"].Value.Replace(',', '.');
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double actual))
                return;

            if (Math.Abs(actual - exactWriteSpeed.Value) < 0.25)
            {
                log?.Report($"Velocidad confirmada por cdrecord: {actual:0.#}x.");
                return;
            }

            speedMismatchMessage =
                $"Pediste {exactWriteSpeed.Value}x, pero la grabadora/cdrecord seleccionó {actual:0.#}x. " +
                "dvdrtool canceló durante la cuenta regresiva, antes de iniciar la escritura, para no grabar a una velocidad distinta.";

            if (Interlocked.Exchange(ref speedAbortRequested, 1) == 0)
            {
                log?.Report("SEGURIDAD DE VELOCIDAD: " + speedMismatchMessage);
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            stdout.Enqueue(e.Data);
            log?.Report(e.Data);
            CheckSelectedWriteSpeed(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            stderr.Enqueue(e.Data);
            log?.Report(e.Data);
            CheckSelectedWriteSpeed(e.Data);
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"No se pudo iniciar {Path.GetFileName(executable)}.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"No se pudo ejecutar {executable}. Verifica la carpeta de cdrtools.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }

        string output = string.Join(Environment.NewLine, stdout);
        string error = string.Join(Environment.NewLine, stderr);
        var result = new CommandResult(process.ExitCode, output, error);

        if (!string.IsNullOrWhiteSpace(speedMismatchMessage))
            throw new InvalidOperationException(speedMismatchMessage);

        if (process.ExitCode != 0 && !allowNonZeroExitCode)
        {
            string all = string.Join(Environment.NewLine, stderr.Concat(stdout));
            Match negativeRemaining = BlocksRemainingRegex.Match(all);
            if (negativeRemaining.Success
                && long.TryParse(negativeRemaining.Groups["value"].Value, out long remaining)
                && remaining < 0)
            {
                long excess = -remaining;
                throw new InvalidOperationException(
                    $"El contenido excede la capacidad del CD en aproximadamente {FormatCdTime(excess)} " +
                    $"({excess * 2048d / (1024d * 1024d):0.0} MiB equivalentes). " +
                    "Reduce las pistas o los datos. El mensaje 'Disk Is not erasable' es normal para un CD-R.");
            }

            string detail = string.Join(Environment.NewLine,
                stderr.Concat(stdout).Reverse().Take(24).Reverse());
            if (string.IsNullOrWhiteSpace(detail)) detail = $"Código de salida: {process.ExitCode}.";

            if (all.Contains("Cannot open SCSI driver", StringComparison.OrdinalIgnoreCase)
                || all.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || all.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "cdrtools no pudo acceder directamente a la grabadora. dvdrtool 2.1 solicita permisos de administrador; " +
                    "si aun así ocurre, comprueba que ninguna otra aplicación esté usando la unidad y vuelve a insertarla." +
                    Environment.NewLine + detail);
            }

            if (all.Contains("No media", StringComparison.OrdinalIgnoreCase)
                || all.Contains("No disk", StringComparison.OrdinalIgnoreCase)
                || all.Contains("not ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "La grabadora no detecta un CD listo para escribir. Inserta un CD-R virgen o CD-RW vacío, espera unos segundos y vuelve a intentar." +
                    Environment.NewLine + detail);
            }

            throw new InvalidOperationException($"{Path.GetFileName(executable)} terminó con error.{Environment.NewLine}{detail}");
        }

        return result;
    }

    private static void ConfigureCygwinPath(ProcessStartInfo psi, string toolsDirectory)
    {
        var entries = new List<string> { toolsDirectory };
        string? toolsParent = Directory.GetParent(toolsDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(toolsParent))
        {
            string cygwin = Path.Combine(toolsParent, "cygwin");
            if (Directory.Exists(cygwin)) entries.Add(cygwin);
        }

        string existing = psi.Environment.TryGetValue("PATH", out string? current)
            ? current ?? ""
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!string.IsNullOrWhiteSpace(existing)) entries.Add(existing);
        psi.Environment["PATH"] = string.Join(Path.PathSeparator.ToString(), entries);
    }

    private static bool LooksLikeOpticalDevice(string type)
    {
        return type.Contains("CD", StringComparison.OrdinalIgnoreCase)
            || type.Contains("DVD", StringComparison.OrdinalIgnoreCase)
            || type.Contains("BD", StringComparison.OrdinalIgnoreCase)
            || type.Contains("WORM", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddProgramFilesCandidate(List<string> candidates, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        candidates.Add(Path.Combine(root, "cdrtfe", "tools", "cdrtools"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void EnsureTools(string directory)
    {
        if (!IsValidToolsDirectory(directory))
            throw new DirectoryNotFoundException("No se encontraron cdrecord.exe y mkisofs.exe en la carpeta de cdrtools seleccionada.");
    }

    private static string SanitizeVolumeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) value = "CD_EXTRA";
        char[] chars = value.Trim().ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')
            .ToArray();
        string label = new(chars);
        if (label.Length == 0) label = "CD_EXTRA";
        return label.Length > 32 ? label[..32] : label;
    }

    private static string LastMeaningfulLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "sin salida adicional";
        return string.Join(" | ", text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(Math.Max(1, count)));
    }

    private static string FormatArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string AllOutput => string.Join(Environment.NewLine,
            new[] { StandardOutput, StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}
