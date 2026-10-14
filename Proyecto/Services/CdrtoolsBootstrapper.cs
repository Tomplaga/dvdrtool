using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

/// <summary>
/// Instala el motor cdrtools dentro de la carpeta portable de dvdrtool.
/// Descarga el paquete portable oficial de cdrtfe únicamente como contenedor,
/// verifica su SHA-256 y extrae solo la carpeta que contiene cdrecord/mkisofs.
/// cdrtfe no queda instalado en Windows.
/// </summary>
public static class CdrtoolsBootstrapper
{
    public const string PackageVersion = "cdrtfe 1.5.9.1 / cdrtools 3.02a10";
    public const string ExpectedSha256 = "86A6D8AD02351303AB2EC6DCFA9B2E97514A2A6586751ADABC5C6CA10E117E12";

    private static readonly string[] PackageUrls =
    {
        "https://downloads.sourceforge.net/project/cdrtfe/cdrtfe/cdrtfe%201.5.9/cdrtfe-1.5.9.1.zip",
        "https://sourceforge.net/projects/cdrtfe/files/cdrtfe/cdrtfe%201.5.9/cdrtfe-1.5.9.1.zip/download"
    };

    public static string LocalToolsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "tools", "cdrtools");

    public static string LocalCygwinDirectory =>
        Path.Combine(AppContext.BaseDirectory, "tools", "cygwin");

    public static bool IsLocalEngineComplete()
    {
        return CdExtraBurner.IsValidToolsDirectory(LocalToolsDirectory)
            && (File.Exists(Path.Combine(LocalToolsDirectory, "cygwin1.dll"))
                || File.Exists(Path.Combine(LocalCygwinDirectory, "cygwin1.dll")));
    }

    public static async Task<string> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsLocalEngineComplete())
            return LocalToolsDirectory;

        string workRoot = Path.Combine(
            Path.GetTempPath(),
            $"dvdrtool_cdrtools_{Environment.ProcessId}_{Guid.NewGuid():N}");
        string zipPath = Path.Combine(workRoot, "cdrtfe-1.5.9.1.zip");
        string extractRoot = Path.Combine(workRoot, "extract");
        Directory.CreateDirectory(workRoot);

        try
        {
            Exception? lastError = null;
            bool verified = false;

            foreach (string url in PackageUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(zipPath);
                try
                {
                    progress?.Report("Descargando motor cdrtools desde SourceForge…");
                    await DownloadAsync(url, zipPath, cancellationToken);

                    progress?.Report("Verificando integridad SHA-256…");
                    string hash = await ComputeSha256Async(zipPath, cancellationToken);
                    if (!hash.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = new InvalidDataException(
                            $"La descarga no coincide con el SHA-256 oficial. Esperado: {ExpectedSha256}; recibido: {hash}.");
                        continue;
                    }

                    verified = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }

            if (!verified)
            {
                throw new InvalidOperationException(
                    "No se pudo descargar y verificar el motor cdrtools desde SourceForge. " +
                    "Comprueba tu conexión a Internet e inténtalo otra vez.",
                    lastError);
            }

            progress?.Report("Extrayendo cdrtools…");
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

            string? sourceTools = Directory
                .EnumerateFiles(extractRoot, "cdrecord.exe", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .FirstOrDefault(path => File.Exists(Path.Combine(path!, "mkisofs.exe")));

            if (string.IsNullOrWhiteSpace(sourceTools))
            {
                throw new InvalidDataException(
                    "El paquete descargado no contiene una carpeta válida con cdrecord.exe y mkisofs.exe.");
            }

            // Los binarios Win32 de cdrtools incluidos por cdrtfe usan Cygwin.
            // cdrtfe guarda estas DLL en tools\cygwin y añade esa carpeta al PATH.
            // Buscamos cygwin1.dll en todo el paquete y copiamos todas las DLL cyg*.dll
            // de esa misma carpeta para que cdrecord/mkisofs puedan arrancar.
            string? cygwin1 = Directory
                .EnumerateFiles(extractRoot, "cygwin1.dll", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(cygwin1))
            {
                throw new InvalidDataException(
                    "El paquete descargado contiene cdrtools, pero no incluye cygwin1.dll. " +
                    "No se instalará un motor incompleto.");
            }

            string sourceCygwin = Path.GetDirectoryName(cygwin1)!;

            progress?.Report("Instalando cdrtools y las DLL de Cygwin dentro de dvdrtool…");
            Directory.CreateDirectory(LocalToolsDirectory);
            CopyDirectory(sourceTools, LocalToolsDirectory);

            Directory.CreateDirectory(LocalCygwinDirectory);
            foreach (string dll in Directory.GetFiles(sourceCygwin, "cyg*.dll", SearchOption.TopDirectoryOnly))
            {
                File.Copy(dll, Path.Combine(LocalCygwinDirectory, Path.GetFileName(dll)), overwrite: true);
            }

            File.WriteAllText(
                Path.Combine(LocalToolsDirectory, "ORIGEN-CDRTOOLS.txt"),
                "Motor instalado automáticamente por dvdrtool.\r\n" +
                $"Paquete: {PackageVersion}\r\n" +
                "Origen: https://sourceforge.net/projects/cdrtfe/\r\n" +
                $"SHA-256 del ZIP verificado: {ExpectedSha256}\r\n" +
                "dvdrtool no instala el frontend cdrtfe; copia localmente cdrtools y sus DLL de Cygwin necesarias.\r\n");

            if (!CdExtraBurner.IsValidToolsDirectory(LocalToolsDirectory))
                throw new InvalidDataException("cdrtools se extrajo, pero faltan cdrecord.exe o mkisofs.exe.");

            if (!File.Exists(Path.Combine(LocalCygwinDirectory, "cygwin1.dll"))
                && !File.Exists(Path.Combine(LocalToolsDirectory, "cygwin1.dll")))
            {
                throw new InvalidDataException("cdrtools se extrajo, pero falta cygwin1.dll.");
            }

            progress?.Report("Motor cdrtools + Cygwin listo.");
            return LocalToolsDirectory;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
            }
            catch
            {
                // Los archivos temporales no deben impedir que dvdrtool continúe.
            }
        }
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dvdrtool/2.6.5");

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
            bufferSize: 81920,
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
            bufferSize: 81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // La siguiente descarga mostrará el error real si el archivo no se puede reemplazar.
        }
    }
}
