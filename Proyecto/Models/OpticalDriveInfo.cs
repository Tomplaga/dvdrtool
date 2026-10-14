namespace CdExtraStudio.Models;

public sealed class OpticalDriveInfo
{
    public string RootPath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsReady { get; init; }
    public string VolumeLabel { get; init; } = "";
    public override string ToString() => DisplayName;
}
