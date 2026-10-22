// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

namespace WinKame.Core;

public enum ExternalTool
{
    FFmpeg,
    LibreOffice,
    Ghostscript,
}

public sealed class ToolMissingException(ExternalTool tool)
    : Exception(T("{0} não está instalado.", ToolLocator.DisplayName(tool)) + " " + ToolLocator.Purpose(tool))
{
    public ExternalTool Tool { get; } = tool;
}

/// <summary>Finds the external programs used for audio/video, documents and PDF compression.</summary>
public static class ToolLocator
{
    private static readonly Dictionary<ExternalTool, string?> Cache = new();

    /// <summary>Paths the user picked in Settings; they win over everything else.</summary>
    public static Dictionary<ExternalTool, string> Overrides { get; } = new();

    public static string DisplayName(ExternalTool t) => t switch
    {
        ExternalTool.FFmpeg => "FFmpeg",
        ExternalTool.LibreOffice => "LibreOffice",
        ExternalTool.Ghostscript => "Ghostscript",
        _ => t.ToString(),
    };

    public static string Purpose(ExternalTool t) => t switch
    {
        ExternalTool.FFmpeg => T("Áudio e vídeo. Já vem incluído no WinKame."),
        ExternalTool.LibreOffice => T("Opcional: slides, DOC/RTF/ODT e planilhas ODS/XLS quando o Microsoft Office não está instalado."),
        ExternalTool.Ghostscript => T("Opcional: comprime PDFs ainda mais."),
        _ => "",
    };

    public static string WingetId(ExternalTool t) => t switch
    {
        ExternalTool.FFmpeg => "Gyan.FFmpeg",
        ExternalTool.LibreOffice => "TheDocumentFoundation.LibreOffice",
        ExternalTool.Ghostscript => "ArtifexSoftware.GhostScript",
        _ => "",
    };

    /// <summary>The FFmpeg that ships inside the app folder.</summary>
    public static string BundledFfmpeg => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");

    /// <summary>True when the tool in use is the copy shipped with WinKame.</summary>
    public static bool IsBundled(ExternalTool t) =>
        string.Equals(Find(t), Path.GetFullPath(t == ExternalTool.FFmpeg ? BundledFfmpeg : "<none>"), StringComparison.OrdinalIgnoreCase);

    public static void Invalidate()
    {
        lock (Cache) Cache.Clear();
    }

    public static string Require(ExternalTool t) => Find(t) ?? throw new ToolMissingException(t);

    public static string? Find(ExternalTool t)
    {
        lock (Cache)
        {
            // Only hits are cached: a tool installed while we run (e.g. via winget) must be found next time.
            if (Cache.TryGetValue(t, out var cached) && cached is not null && File.Exists(cached)) return cached;
            var found = Search(t);
            Cache[t] = found;
            return found;
        }
    }

    /// <summary>ffprobe ships next to ffmpeg in every common build.</summary>
    public static string? FindFfprobe()
    {
        var ff = Find(ExternalTool.FFmpeg);
        if (ff is null) return null;
        var probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
        return File.Exists(probe) ? probe : null;
    }

    private static string? Search(ExternalTool t)
    {
        if (Overrides.TryGetValue(t, out var o) && File.Exists(o)) return o;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools");

        IEnumerable<string> candidates = t switch
        {
            ExternalTool.FFmpeg =>
            [
                Path.Combine(bundled, "ffmpeg.exe"),
                BundledFfmpeg,
                Path.Combine(bundled, "ffmpeg", "bin", "ffmpeg.exe"),
                .. OnPath("ffmpeg.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
                .. Glob(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "Gyan.FFmpeg*", Path.Combine("*", "bin", "ffmpeg.exe")),
                .. Glob(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "BtbN.FFmpeg*", Path.Combine("*", "bin", "ffmpeg.exe")),
                Path.Combine(home, "scoop", "shims", "ffmpeg.exe"),
                @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
            ],
            ExternalTool.LibreOffice =>
            [
                Path.Combine(pf, "LibreOffice", "program", "soffice.com"),
                Path.Combine(pf, "LibreOffice", "program", "soffice.exe"),
                Path.Combine(pf86, "LibreOffice", "program", "soffice.com"),
                Path.Combine(pf86, "LibreOffice", "program", "soffice.exe"),
                .. OnPath("soffice.com"),
                .. OnPath("soffice.exe"),
            ],
            ExternalTool.Ghostscript =>
            [
                .. OnPath("gswin64c.exe"),
                .. Glob(Path.Combine(pf, "gs"), "gs*", Path.Combine("bin", "gswin64c.exe")).OrderDescending(),
                .. Glob(Path.Combine(pf86, "gs"), "gs*", Path.Combine("bin", "gswin32c.exe")).OrderDescending(),
            ],
            _ => [],
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Searches PATH as it is now in the registry, so tools installed after launch are found.</summary>
    private static IEnumerable<string> OnPath(string exe)
    {
        var parts = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in parts.Where(p => p is not null).SelectMany(p => p!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(dir.Trim('"')); }
            catch { continue; }
            if (!seen.Add(expanded)) continue;
            yield return Path.Combine(expanded, exe);
        }
    }

    private static IEnumerable<string> Glob(string root, string dirPattern, string rest)
    {
        if (!Directory.Exists(root)) return [];
        try
        {
            var results = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(root, dirPattern))
            {
                var segments = rest.Split(Path.DirectorySeparatorChar);
                IEnumerable<string> current = [d];
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    var seg = segments[i];
                    current = current.SelectMany(c => seg.Contains('*')
                        ? (Directory.Exists(c) ? Directory.EnumerateDirectories(c, seg) : [])
                        : [Path.Combine(c, seg)]);
                }
                results.AddRange(current.Select(c => Path.Combine(c, segments[^1])));
            }
            return results;
        }
        catch
        {
            return [];
        }
    }
}
