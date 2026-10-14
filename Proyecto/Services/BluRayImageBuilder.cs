using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace CdExtraStudio.Services;

/// <summary>
/// Crea una imagen UDF 2.50 a partir de una estructura Blu-ray (BDMV/CERTIFICATE)
/// usando IMAPI2FS incluido en Windows. No transcodifica ni modifica los archivos.
/// </summary>
public static class BluRayImageBuilder
{
    private const int FsiFileSystemUdf = 4;
    private const int Udf250 = 0x250;

    public static string NormalizeBluRayRoot(string selectedFolder)
    {
        if (string.IsNullOrWhiteSpace(selectedFolder))
            throw new InvalidOperationException("Selecciona una carpeta Blu-ray.");

        string full = Path.GetFullPath(selectedFolder);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("La carpeta Blu-ray seleccionada ya no existe.");

        if (Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("BDMV", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Directory.GetParent(full)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
                full = parent;
        }

        string? bdmv = FindChildDirectory(full, "BDMV");
        if (bdmv is null)
            throw new InvalidOperationException(
                "La carpeta seleccionada no contiene una carpeta BDMV.\n\n" +
                "Selecciona la carpeta raíz del Blu-ray o la propia carpeta BDMV.");

        string? index = Directory.EnumerateFiles(bdmv)
            .FirstOrDefault(p => Path.GetFileName(p).Equals("index.bdmv", StringComparison.OrdinalIgnoreCase));

        if (index is null)
            throw new InvalidOperationException(
                "Se encontró BDMV, pero falta BDMV\\index.bdmv.\n\n" +
                "La carpeta no parece una estructura Blu-ray Video completa.");

        return full;
    }

    public static bool HasCertificateFolder(string root) =>
        FindChildDirectory(root, "CERTIFICATE") is not null;

    public static long CalculateDirectoryBytes(string root)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { }
        }
        return total;
    }

    public static async Task<long> CreateUdf250ImageAsync(
        string selectedFolder,
        string outputIso,
        string volumeName,
        IProgress<(double progress, string status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string root = NormalizeBluRayRoot(selectedFolder);
        long sourceBytes = CalculateDirectoryBytes(root);
        EnsureTempSpace(outputIso, sourceBytes);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((0.05, "Preparando estructura Blu-ray UDF 2.50…"));

            object? imageObj = null;
            object? rootObj = null;
            object? resultObj = null;
            object? streamObj = null;

            try
            {
                Type? type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage");
                if (type is null)
                    throw new InvalidOperationException(
                        "Windows IMAPI2FS no está disponible. No se puede crear la imagen UDF 2.50.");

                imageObj = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException("No se pudo iniciar IMAPI2FS.");

                dynamic image = imageObj;
                image.FileSystemsToCreate = FsiFileSystemUdf;
                image.UDFRevision = Udf250;
                image.VolumeName = SanitizeVolumeName(volumeName);
                image.FreeMediaBlocks = int.MaxValue;

                // UDF 2.50 admite metadatos redundantes. No todos los Windows
                // exponen IFileSystemImage3 mediante automatización, así que es opcional.
                try { image.CreateRedundantUdfMetadataFiles = true; } catch { }

                progress?.Report((0.15, "Agregando BDMV y archivos a la imagen…"));
                rootObj = image.Root;
                dynamic rootDirectory = rootObj;
                rootDirectory.AddTree(root, false);

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((0.35, "Generando sistema de archivos UDF 2.50…"));

                resultObj = image.CreateResultImage();
                dynamic result = resultObj;
                long totalBytes = checked((long)result.TotalBlocks * (long)result.BlockSize);

                streamObj = result.ImageStream;
                IStream imageStream = (IStream)streamObj;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputIso))!);
                using var output = new FileStream(
                    outputIso, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.SequentialScan);

                byte[] buffer = new byte[1024 * 1024];
                IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
                long written = 0;

                try
                {
                    while (written < totalBytes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int request = (int)Math.Min(buffer.Length, totalBytes - written);
                        Marshal.WriteInt32(readPtr, 0);
                        imageStream.Read(buffer, request, readPtr);
                        int read = Marshal.ReadInt32(readPtr);
                        if (read <= 0) break;

                        output.Write(buffer, 0, read);
                        written += read;

                        if (totalBytes > 0)
                        {
                            double p = 0.40 + 0.50 * (written / (double)totalBytes);
                            progress?.Report((Math.Min(0.90, p), "Escribiendo imagen UDF 2.50 temporal…"));
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(readPtr);
                }

                output.Flush(true);

                if (written != totalBytes)
                    throw new IOException(
                        $"La imagen UDF quedó incompleta: {written} de {totalBytes} bytes.");

                progress?.Report((0.92, "Imagen UDF 2.50 lista."));
                return totalBytes;
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "Windows no pudo crear la imagen UDF 2.50 para la carpeta Blu-ray.\n\n" +
                    ex.Message, ex);
            }
            finally
            {
                ReleaseCom(streamObj);
                ReleaseCom(resultObj);
                ReleaseCom(rootObj);
                ReleaseCom(imageObj);
            }
        }, cancellationToken);
    }

    private static string? FindChildDirectory(string root, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeVolumeName(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "BLURAY_VIDEO" : value.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        if (name.Length > 32) name = name[..32];
        return string.IsNullOrWhiteSpace(name) ? "BLURAY_VIDEO" : name;
    }

    private static void EnsureTempSpace(string outputIso, long sourceBytes)
    {
        string full = Path.GetFullPath(outputIso);
        string? rootPath = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(rootPath)) return;

        try
        {
            var drive = new DriveInfo(rootPath);
            long required = sourceBytes + Math.Max(512L * 1024 * 1024, sourceBytes / 50);
            if (drive.AvailableFreeSpace < required)
            {
                throw new IOException(
                    "No hay espacio temporal suficiente para crear la imagen Blu-ray.\n\n" +
                    $"Necesario aproximadamente: {required / 1024.0 / 1024 / 1024:0.0} GB\n" +
                    $"Libre: {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // Si Windows no permite consultar la unidad, IMAPI dará el error real.
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null) return;
        try
        {
            if (Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch { }
    }
}
