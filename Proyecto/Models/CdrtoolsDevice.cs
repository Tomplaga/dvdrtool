namespace CdExtraStudio.Models;

public sealed class CdrtoolsDevice
{
    public string DeviceSpec { get; init; } = "";
    public string Description { get; init; } = "";
    public string BusAddress { get; init; } = "";

    public override string ToString() => string.IsNullOrWhiteSpace(Description)
        ? DeviceSpec
        : $"{Description}  ·  {DeviceSpec}";
}
