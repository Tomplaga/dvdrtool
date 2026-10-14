using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

/// <summary>
/// Instala FFmpeg/ffprobe dentro de la carpeta portable de dvdrtool.
/// Usa el build "release essentials" de gyan.dev, enlazado desde la página
/// oficial de descargas de FFmpeg para Windows. El SHA-256 se descarga desde
/// el mismo proveedor y se verifica antes de extraer los ejecutables.
/// </summary>
public static class FfmpegBootstrapper
{
    public const string PackageName = "FFmpeg release essentials (Windows x64)";

    private const string PackageUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string Sha256Url =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";

    public static string LocalDirectory =>
        Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");

    public static string LocalFfmpegPath =>
        Path.Combine(LocalDirectory, "ffmpeg.exe");

    public static string LocalFfprobePath =>
        Path.Combine(LocalDirectory, "ffprobe.exe");

    public static bool IsLocalInstallComplete() =>
        File.Exists(LocalFfmpegPath) && File.Exists(LocalFfprobePath);

    public static async Task<string> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsLocalInstallComplete())
            return LocalFfmpegPath;

        string workRoot = Path.Combine(
            Path.GetTempPath(),
            $"dvdrtool_ffmpeg_{Environment.ProcessId}_{Guid.NewGuid():N}");
        string zipPath = Path.Combine(workRoot, "ffmpeg-release-essentials.zip");
        string extractRoot = Path.Combine(workRoot, "extract");
        Directory.CreateDirectory(workRoot);

        try
        {
            progress?.Report("Consultando SHA-256 de FFmpeg…");
            string expectedHash = await DownloadTextAsync(Sha256Url, cancellationToken);
            expectedHash = NormalizeSha256(expectedHash);
            if (expectedHash.Length != 64)
                throw new InvalidDataException("El proveedor no devolvió un SHA-256 válido para FFmpeg.");

            progress?.Report("Descargando FFmpeg release essentials…");
            await DownloadFileAsync(PackageUrl, zipPath, cancellationToken);

            progress?.Report("Verificando integridad SHA-256 de FFmpeg…");
            string actualHash = await ComputeSha256Async(zipPath, cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"La descarga de FFmpeg no coincide con el SHA-256 publicado. " +
                    $"Esperado: {expectedHash}; recibido: {actualHash}.");
            }

            progress?.Report("Extrayendo FFmpeg y ffprobe…");
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

            string? sourceFfmpeg = Directory
                .EnumerateFiles(extractRoot, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            string? sourceFfprobe = Directory
                .EnumerateFiles(extractRoot, "ffprobe.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(sourceFfmpeg) || string.IsNullOrWhiteSpace(sourceFfprobe))
                throw new InvalidDataException("El paquete de FFmpeg no contiene ffmpeg.exe y ffprobe.exe.");

            Directory.CreateDirectory(LocalDirectory);
            File.Copy(sourceFfmpeg, LocalFfmpegPath, overwrite: true);
            File.Copy(sourceFfprobe, LocalFfprobePath, overwrite: true);

            string? sourceFfplay = Directory
                .EnumerateFiles(extractRoot, "ffplay.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sourceFfplay))
                File.Copy(sourceFfplay, Path.Combine(LocalDirectory, "ffplay.exe"), overwrite: true);

            File.WriteAllText(
                Path.Combine(LocalDirectory, "ORIGEN-FFMPEG.txt"),
                "FFmpeg instalado automáticamente por dvdrtool.\r\n" +
                $"Paquete: {PackageName}\r\n" +
                "Proveedor Windows: https://www.gyan.dev/ffmpeg/builds/\r\n" +
                "Página oficial FFmpeg: https://ffmpeg.org/download.html\r\n" +
                $"SHA-256 verificado: {expectedHash}\r\n" +
                "dvdrtool usa ffmpeg.exe para decodificar/remuestrear audio y ffprobe.exe para leer duración/metadatos.\r\n");

            if (!IsLocalInstallComplete())
                throw new InvalidDataException("FFmpeg se extrajo, pero faltan ffmpeg.exe o ffprobe.exe.");

            progress?.Report("FFmpeg universal listo.");
            return LocalFfmpegPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
            }
            catch
            {
                // Los temporales no deben impedir que dvdrtool continúe.
            }
        }
    }

    private static string NormalizeSha256(string text)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 0) return "";
        string first = value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        return first.Trim().ToUpperInvariant();
    }

    private static async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dvdrtool/2.6.6");
        return await client.GetStringAsync(url, cancellationToken);
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dvdrtool/2.6.6");

        using HttpResponseMessage response = await client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131072,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
