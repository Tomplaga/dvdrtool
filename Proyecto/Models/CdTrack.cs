using System;

namespace CdExtraStudio.Models;

public sealed class CdTrack
{
    public int Number { get; init; }
    public int StartLba { get; init; }
    public int EndLba { get; init; }
    public bool IsAudio { get; init; }
    public int SectorCount => Math.Max(0, EndLba - StartLba);
    public TimeSpan Duration => TimeSpan.FromSeconds(SectorCount / 75.0);
    public string TypeText => IsAudio ? "Audio" : "Datos";
    public string DurationText => Duration.ToString(@"mm\:ss");
    public string Display => $"Pista {Number:00}  ·  {DurationText}  ·  {TypeText}";
}
