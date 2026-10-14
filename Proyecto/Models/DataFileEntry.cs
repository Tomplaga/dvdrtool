using System;

namespace CdExtraStudio.Models;

public sealed class DataFileEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long Size { get; init; }
    public string SizeText => Size < 1024 ? $"{Size} B" :
        Size < 1024 * 1024 ? $"{Size / 1024.0:0.0} KB" :
        Size < 1024L * 1024 * 1024 ? $"{Size / 1024.0 / 1024:0.0} MB" : $"{Size / 1024.0 / 1024 / 1024:0.00} GB";
}
