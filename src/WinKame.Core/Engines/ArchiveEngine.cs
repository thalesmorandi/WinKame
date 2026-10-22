using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace WinKame.Core.Engines;

public static class ArchiveEngine
{
    /// <summary>Extracts into a new folder beside the archive. Returns that folder.</summary>
    public static string Extract(string archive, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = OutputPaths.UniqueDir(OutputPaths.DirFor(archive), OutputPaths.StemOf(archive));
        Directory.CreateDirectory(dest);
        ExtractTo(archive, dest, progress, ct);
        return FlattenSingleRoot(dest);
    }

    public static void ExtractTo(string archive, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        var ext = Formats.Ext(archive);
        if (ext is "gz" or "bz2" or "xz" && !LooksLikeTar(archive))
        {
            // A single compressed file, e.g. "notes.txt.gz".
            using var input = File.OpenRead(archive);
            using var reader = ReaderFactory.OpenReader(input);
            if (reader.MoveToNextEntry())
            {
                var name = Path.GetFileNameWithoutExtension(archive);
                using var es = reader.OpenEntryStream();
                using var outFile = File.Create(Path.Combine(dest, name));
                es.CopyTo(outFile);
            }
            return;
        }

        if (ext is "zip" or "rar" or "7z")
        {
            using var arc = ArchiveFactory.OpenArchive(archive);
            var entries = arc.Entries.Where(e => !e.IsDirectory).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                var target = SafePath(dest, entry.Key);
                if (target is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var es = entry.OpenEntryStream())
                using (var fs = File.Create(target))
                {
                    es.CopyTo(fs);
                }
                if (entry.LastModifiedTime is { } t) TrySetTime(target, t);
                progress?.Report((i + 1) / (double)entries.Count);
            }
            return;
        }

        // Tar and compressed tar, streamed.
        using (var stream = File.OpenRead(archive))
        using (var reader = ReaderFactory.OpenReader(stream))
        {
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                var target = SafePath(dest, reader.Entry.Key);
                if (target is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var es = reader.OpenEntryStream())
                using (var fs = File.Create(target))
                {
                    es.CopyTo(fs);
                }
                if (reader.Entry.LastModifiedTime is { } t) TrySetTime(target, t);
                progress?.Report(Math.Min(0.99, stream.Position / (double)Math.Max(1, stream.Length)));
            }
        }
    }

    private static bool LooksLikeTar(string archive)
    {
        var name = Path.GetFileName(archive).ToLowerInvariant();
        return name.Contains(".tar.") || name.EndsWith(".tgz") || name.EndsWith(".tbz2") || name.EndsWith(".txz");
    }

    /// <summary>Resolves an entry path inside <paramref name="root"/>, refusing anything that escapes it.</summary>
    private static string? SafePath(string root, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var clean = key.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        foreach (var c in Path.GetInvalidPathChars()) clean = clean.Replace(c, '_');
        var full = Path.GetFullPath(Path.Combine(rootFull, clean));
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void TrySetTime(string path, DateTime t)
    {
        try { File.SetLastWriteTime(path, t); }
        catch { /* not important */ }
    }

    /// <summary>"x/x/files" becomes "x/files" when the archive had a single top folder.</summary>
    private static string FlattenSingleRoot(string dest)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(dest);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
            {
                var inner = entries[0];
                var parent = Path.GetDirectoryName(dest)!;
                var temp = dest + ".wq-tmp";
                Directory.Move(inner, temp);
                Directory.Delete(dest);
                var final = OutputPaths.UniqueDir(parent, Path.GetFileName(inner));
                Directory.Move(temp, final);
                return final;
            }
        }
        catch
        {
            // Keep the nested layout if anything goes wrong.
        }
        return dest;
    }

    /// <summary>Packs files and folders into a ZIP named after the first item (or "Arquivos" for several).</summary>
    public static string ZipItems(IReadOnlyList<string> items, IProgress<double>? progress, CancellationToken ct)
    {
        var name = items.Count == 1 ? OutputPaths.StemOf(items[0]) : T("Arquivos");
        var outPath = OutputPaths.Unique(OutputPaths.DirFor(items[0]), name, "zip");
        var files = new List<(string Path, string Entry)>();
        foreach (var item in items)
        {
            if (Directory.Exists(item))
            {
                var baseName = Path.GetFileName(item.TrimEnd(Path.DirectorySeparatorChar));
                var any = false;
                foreach (var f in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                {
                    files.Add((f, Path.Combine(baseName, Path.GetRelativePath(item, f)).Replace('\\', '/')));
                    any = true;
                }
                if (!any) files.Add((item, baseName + "/"));
            }
            else
            {
                files.Add((item, Path.GetFileName(item)));
            }
        }

        using (var zip = ZipFile.Open(outPath, ZipArchiveMode.Create))
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (path, entry) = files[i];
                if (entry.EndsWith('/')) zip.CreateEntry(entry);
                else zip.CreateEntryFromFile(path, entry, CompressionLevel.Optimal);
                progress?.Report((i + 1) / (double)files.Count);
            }
        }
        return outPath;
    }

    public static void PackDirectory(string sourceDir, string target, string outPath)
    {
        switch (Formats.Canonical(target))
        {
            case "zip":
                ZipFile.CreateFromDirectory(sourceDir, outPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                break;
            case "tar":
                TarFile.CreateFromDirectory(sourceDir, outPath, includeBaseDirectory: false);
                break;
            case "tar.gz":
            {
                using var fs = File.Create(outPath);
                using var gz = new GZipStream(fs, CompressionLevel.Optimal);
                TarFile.CreateFromDirectory(sourceDir, gz, includeBaseDirectory: false);
                break;
            }
            default:
                throw new NotSupportedException(target);
        }
    }

    public static string Convert(string archive, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var temp = OutputPaths.NewTempDir();
        try
        {
            ExtractTo(archive, temp, new Progress<double>(v => progress?.Report(v * 0.6)), ct);
            var outPath = OutputPaths.For(archive, target);
            PackDirectory(temp, target, outPath);
            progress?.Report(1);
            return outPath;
        }
        finally
        {
            OutputPaths.TryDelete(temp);
        }
    }
}
