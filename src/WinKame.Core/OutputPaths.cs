// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

namespace WinKame.Core;

/// <summary>Chooses where results are written, never overwriting an existing file.</summary>
public static class OutputPaths
{
    /// <summary>When set, every result goes to this folder instead of beside the original.</summary>
    public static string? OverrideDir { get; set; }

    public static string DirFor(string input)
    {
        if (!string.IsNullOrWhiteSpace(OverrideDir))
        {
            Directory.CreateDirectory(OverrideDir);
            return OverrideDir;
        }
        var full = Path.GetFullPath(input);
        return Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar)) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    /// <summary>File name without any extension, including compound ones like ".tar.gz".</summary>
    public static string StemOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Directory.Exists(path)) return name;
        var ext = Formats.Ext(path);
        return ext.Length > 0 && name.Length > ext.Length + 1 ? name[..^(ext.Length + 1)] : name;
    }

    /// <summary>"name.ext", or "name (2).ext" and so on when that already exists.</summary>
    public static string Unique(string dir, string baseName, string ext)
    {
        ext = ext.TrimStart('.');
        string Make(int i) => Path.Combine(dir, (i == 1 ? baseName : $"{baseName} ({i})") + (ext.Length > 0 ? "." + ext : ""));
        for (var i = 1; ; i++)
        {
            var candidate = Make(i);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static string UniqueDir(string dir, string name) => Unique(dir, name, "");

    /// <summary>Output path for a result derived from <paramref name="input"/>.</summary>
    public static string For(string input, string newExt, string? suffix = null)
    {
        var stem = StemOf(input);
        if (!string.IsNullOrEmpty(suffix)) stem = $"{stem} - {T(suffix)}";
        return Unique(DirFor(input), stem, newExt);
    }

    /// <summary>A new folder beside the input, e.g. "video - partes".</summary>
    public static string FolderFor(string input, string suffix)
    {
        var dir = UniqueDir(DirFor(input), $"{StemOf(input)} - {T(suffix)}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A fresh temporary folder that the caller deletes.</summary>
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinKame", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Moves a temporary file to its final location, picking a free name.</summary>
    public static string MoveInto(string tempFile, string finalPath)
    {
        if (File.Exists(finalPath))
        {
            finalPath = Unique(Path.GetDirectoryName(finalPath)!, Path.GetFileNameWithoutExtension(finalPath), Path.GetExtension(finalPath));
        }
        File.Move(tempFile, finalPath);
        return finalPath;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
