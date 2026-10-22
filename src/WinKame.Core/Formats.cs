// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

namespace WinKame.Core;

public enum FileKind
{
    Unknown,
    Folder,
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Slides,
    Spreadsheet,
    Epub,
    Subtitle,
    Archive,
}

/// <summary>Maps file extensions to kinds and lists what each kind can be converted to.</summary>
public static class Formats
{
    private static readonly Dictionary<string, FileKind> ByExt = new(StringComparer.OrdinalIgnoreCase);

    // Extensions that mean the same format; the value is the canonical spelling used as a target.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = "jpg", ["jfif"] = "jpg", ["jpe"] = "jpg",
        ["tif"] = "tiff",
        ["heif"] = "heic",
        ["htm"] = "html",
        ["markdown"] = "md",
        ["aif"] = "aiff",
        ["tgz"] = "tar.gz",
        ["oga"] = "ogg",
        ["m4v"] = "mp4",
    };

    private static readonly Dictionary<FileKind, string[]> Targets = new()
    {
        [FileKind.Image] = ["jpg", "png", "webp", "heic", "avif", "tiff", "bmp", "gif", "ico", "pdf"],
        [FileKind.Video] = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "gif", "mp3", "m4a", "wav"],
        [FileKind.Audio] = ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"],
        [FileKind.Pdf] = ["docx", "txt", "md", "html", "epub", "png", "jpg", "pptx", "xlsx"],
        [FileKind.Document] = ["pdf", "docx", "odt", "rtf", "txt", "md", "html", "epub", "png", "jpg"],
        [FileKind.Slides] = ["pdf", "pptx", "odp", "png", "jpg"],
        [FileKind.Spreadsheet] = ["xlsx", "ods", "csv", "tsv", "pdf"],
        [FileKind.Epub] = ["pdf", "docx", "txt", "md", "html"],
        [FileKind.Subtitle] = ["srt", "vtt", "txt"],
        [FileKind.Archive] = ["zip", "tar", "tar.gz"],
    };

    /// <summary>Optional filter for image targets the local codec can actually write (set by the image engine).</summary>
    public static Func<string, bool>? ImageTargetFilter { get; set; }

    static Formats()
    {
        void Add(FileKind kind, params string[] exts)
        {
            foreach (var e in exts) ByExt[e] = kind;
        }

        Add(FileKind.Image, "jpg", "jpeg", "jfif", "jpe", "png", "webp", "heic", "heif", "tif", "tiff", "avif", "bmp", "gif", "ico", "svg", "tga", "psd", "jxl");
        Add(FileKind.Video, "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "ts", "mts", "m2ts", "3gp", "ogv");
        Add(FileKind.Audio, "mp3", "m4a", "aac", "wav", "flac", "ogg", "oga", "opus", "aiff", "aif", "wma", "amr", "mka");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Document, "docx", "doc", "odt", "rtf", "txt", "md", "markdown", "html", "htm", "pages");
        Add(FileKind.Slides, "pptx", "ppt", "odp", "key");
        Add(FileKind.Spreadsheet, "xlsx", "xls", "ods", "csv", "tsv", "numbers");
        Add(FileKind.Epub, "epub");
        Add(FileKind.Subtitle, "srt", "vtt");
        Add(FileKind.Archive, "zip", "tar", "gz", "tgz", "rar", "7z", "bz2", "xz", "tar.gz", "tar.bz2", "tar.xz");
    }

    /// <summary>Lower-case extension without the dot. Compound archive extensions like "tar.gz" are kept whole.</summary>
    public static string Ext(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
        {
            if (name.EndsWith(compound, StringComparison.Ordinal)) return compound[1..];
        }
        return Path.GetExtension(name).TrimStart('.');
    }

    public static string Canonical(string ext)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        return Aliases.TryGetValue(ext, out var c) ? c : ext;
    }

    public static FileKind KindOf(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        return ByExt.TryGetValue(Ext(path), out var k) ? k : FileKind.Unknown;
    }

    public static FileKind KindOfExt(string ext) =>
        ByExt.TryGetValue(ext.TrimStart('.'), out var k) ? k : FileKind.Unknown;

    /// <summary>Formats a single file can be converted to, without its own format.</summary>
    public static IReadOnlyList<string> TargetsFor(string path)
    {
        var kind = KindOf(path);
        if (!Targets.TryGetValue(kind, out var list)) return [];
        var own = Canonical(Ext(path));
        IEnumerable<string> result = list.Where(t => t != own);
        if (kind == FileKind.Image)
        {
            if (ImageTargetFilter is { } f) result = result.Where(t => t == "pdf" || f(t));
            if (own == "gif") result = result.Append("mp4").Append("webm");
        }
        if (kind == FileKind.Archive && own is "gz" or "bz2" or "xz")
        {
            // A bare .gz may hold one file, not a tar; only extraction makes sense.
            result = result.Where(t => t != "tar.gz");
        }
        return result.ToList();
    }

    /// <summary>Targets valid for every file in the selection, in the order of the first file's list.</summary>
    public static IReadOnlyList<string> CommonTargets(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        var perFile = paths.Select(p => (Own: Canonical(Ext(p)), Targets: TargetsFor(p).ToHashSet())).ToList();
        // Candidates in a stable order: each file's target list, then the files' own formats.
        var candidates = paths.SelectMany(TargetsFor).Concat(perFile.Select(f => f.Own)).Distinct();
        // A file already in the target format is simply skipped, so it doesn't veto that target.
        return candidates
            .Where(t => perFile.All(f => f.Targets.Contains(t) || f.Own == t))
            .Where(t => perFile.Any(f => f.Own != t))
            .ToList();
    }

    /// <summary>The single kind shared by every path, or Unknown when they differ.</summary>
    public static FileKind CommonKind(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return FileKind.Unknown;
        var k = KindOf(paths[0]);
        return paths.All(p => KindOf(p) == k) ? k : FileKind.Unknown;
    }

    public static string Label(string target) => target.ToUpperInvariant();
}
