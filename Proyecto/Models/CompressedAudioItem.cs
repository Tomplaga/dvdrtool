using System;
using System.IO;

namespace CdExtraStudio.Models;

/// <summary>
/// Archivo de audio almacenado en la sesión de datos del CD Extra.
/// Puede conservar el formato original o ser convertido a otro formato.
/// El nombre de la clase se mantiene por compatibilidad con versiones anteriores.
/// </summary>
public sealed class CompressedAudioItem
{
    public required string SourcePath { get; init; }
    public required string DataPath { get; init; }
    public string OutputFormat { get; init; } = "Original";

    // Alias de compatibilidad con código/proyectos de versiones 1.8 y anteriores.
    public string Mp3Path => DataPath;

    public string Name => Path.GetFileName(SourcePath);
    public string StoredName => Path.GetFileName(DataPath);
    public string StoredExtension => Path.GetExtension(DataPath).TrimStart('.').ToUpperInvariant();
    public string Display => $"{Path.GetFileNameWithoutExtension(SourcePath)}  ·  {StoredExtension} / Datos";

    public long SizeBytes
    {
        get
        {
            try { return File.Exists(DataPath) ? new FileInfo(DataPath).Length : 0; }
            catch { return 0; }
        }
    }

    public string SizeText => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / 1024.0 / 1024.0:0.0} MB";
    }

    public override string ToString() => Display;
}
