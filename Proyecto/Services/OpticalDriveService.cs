using CdExtraStudio.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace CdExtraStudio.Services;

public static class OpticalDriveService
{
    public static IReadOnlyList<OpticalDriveInfo> GetDrives()
    {
        var result = new List<OpticalDriveInfo>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            var ready = false;
            var label = "";
            try
            {
                ready = drive.IsReady;
                if (ready) label = drive.VolumeLabel;
            }
            catch { }

            result.Add(new OpticalDriveInfo
            {
                RootPath = drive.RootDirectory.FullName,
                IsReady = ready,
                VolumeLabel = label,
                DisplayName = $"{drive.Name.TrimEnd('\\')}  ·  {(ready ? (string.IsNullOrWhiteSpace(label) ? "Disco insertado" : label) : "Sin disco")}" 
            });
        }
        return result;
    }

    public static IReadOnlyList<DataFileEntry> ReadDataFiles(string rootPath)
    {
        var entries = new List<DataFileEntry>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return entries;
        try
        {
            foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(path);
                    entries.Add(new DataFileEntry
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        RelativePath = Path.GetRelativePath(rootPath, fi.FullName),
                        Size = fi.Length
                    });
                }
                catch { }
            }
        }
        catch { }
        return entries;
    }
}
