using System;

namespace CdExtraStudio.Models;

public sealed class DiscCapacityInfo
{
    public long MediaSectors { get; init; }
    public long AudioSectors { get; init; }
    public long DataSectors { get; init; }
    public long OverheadSectors { get; init; }
    public long SafetySectors { get; init; }

    // En la comprobación rápida puede haber formatos cuya duración no pueda
    // leerse sin decodificarlos. En ese caso AudioSectors representa solo las
    // pistas conocidas y la comprobación exacta se repite después de convertir,
    // justo antes de escribir el disco.
    public bool AudioEstimateComplete { get; init; } = true;
    public int UnknownAudioCount { get; init; }
    public string? UnknownAudioNames { get; init; }

    public long RequiredSectors => AudioSectors + DataSectors + OverheadSectors + SafetySectors;
    public bool IsKnown => MediaSectors > 0;
    public bool CanDetermineFit => IsKnown && AudioEstimateComplete;

    // Aunque falten duraciones, si la parte ya conocida supera el disco sabemos
    // con certeza que no cabe. Si no lo supera, se deja continuar hasta el
    // preflight exacto de BurnAsync.
    public bool DefinitelyExceeds => IsKnown && RequiredSectors > MediaSectors;
    public bool Fits => !DefinitelyExceeds && (!CanDetermineFit || RequiredSectors <= MediaSectors);
    public long RemainingSectors => IsKnown ? MediaSectors - RequiredSectors : 0;
    public double UsagePercent => IsKnown && MediaSectors > 0
        ? Math.Max(0, (double)RequiredSectors / MediaSectors * 100.0)
        : 0;

    public static string FormatCdTime(long sectors)
    {
        long value = Math.Abs(sectors);
        long totalSeconds = value / 75;
        long minutes = totalSeconds / 60;
        long seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00}";
    }

    public static string FormatDataMiB(long sectors) =>
        $"{sectors * 2048d / (1024d * 1024d):0.0} MiB";
}
