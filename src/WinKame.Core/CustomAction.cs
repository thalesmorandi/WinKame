// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WinKame.Core;

public enum CustomRunMode
{
    /// <summary>Runs the command once for each file.</summary>
    PerFile,
    /// <summary>Runs the command once with every file ({paths}).</summary>
    AllAtOnce,
}

/// <summary>A user-defined command that shows up as a slice of a wheel.</summary>
public sealed class CustomAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = T("Nova ação");

    /// <summary>One or two characters shown on the slice (emoji, letter or a Segoe icon).</summary>
    public string Glyph { get; set; } = "";

    /// <summary>"*" for everything, "folder" for folders, or a list like "jpg, png".</summary>
    public string Extensions { get; set; } = "*";

    /// <summary>Which wheel shows it: custom, tools, actions or convert.</summary>
    public string Wheel { get; set; } = "custom";

    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "\"{path}\"";
    public string? WorkingDirectory { get; set; }
    public CustomRunMode Mode { get; set; } = CustomRunMode.PerFile;
    public bool ShowWindow { get; set; }
    public bool WaitForExit { get; set; } = true;
    public bool Enabled { get; set; } = true;

    public CustomAction Clone() => (CustomAction)MemberwiseClone();

    public bool Matches(IReadOnlyList<string> files)
    {
        if (!Enabled || files.Count == 0) return false;
        var spec = (Extensions ?? "*").Trim();
        if (spec.Length == 0 || spec == "*") return true;
        var parts = spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .ToHashSet();
        return files.All(f =>
        {
            if (Directory.Exists(f)) return parts.Contains("folder") || parts.Contains("pasta");
            var ext = Formats.Ext(f);
            return parts.Contains(ext) || parts.Contains(Formats.Canonical(ext)) || parts.Contains("file") || parts.Contains("arquivo");
        });
    }

    /// <summary>Ready-made actions offered in Settings.</summary>
    public static IEnumerable<CustomAction> Examples() =>
    [
        new()
        {
            Name = T("Abrir no VS Code"), Glyph = "", Extensions = "*", Command = "code",
            Arguments = "\"{path}\"", Mode = CustomRunMode.PerFile, WaitForExit = false,
        },
        new()
        {
            Name = "SHA-256", Glyph = "#", Extensions = "*", Command = "powershell.exe",
            Arguments = "-NoProfile -Command \"(Get-FileHash -Algorithm SHA256 -LiteralPath '{path}').Hash | Set-Clipboard\"",
            Mode = CustomRunMode.PerFile, WaitForExit = true,
        },
        new()
        {
            Name = T("Vídeo 720p"), Glyph = "7", Extensions = "mp4, mov, mkv, webm", Command = "ffmpeg",
            Arguments = "-y -i \"{path}\" -vf scale=-2:720 -c:a copy \"{out:mp4}\"", Mode = CustomRunMode.PerFile, ShowWindow = true,
        },
        new()
        {
            Name = T("Renomear com data"), Glyph = "", Extensions = "*", Command = "powershell.exe",
            Arguments = "-NoProfile -Command \"Rename-Item -LiteralPath '{path}' -NewName ('{date}_' + '{filename}')\"",
            Mode = CustomRunMode.PerFile,
        },
        new()
        {
            Name = T("Listar conteúdo"), Glyph = "", Extensions = "folder", Command = "cmd.exe",
            Arguments = "/c dir /s /b \"{path}\" > \"{dir}\\{name} - " + T("conteúdo") + ".txt\"", Mode = CustomRunMode.PerFile,
        },
    ];
}

public static partial class CustomActionRunner
{
    [GeneratedRegex(@"\{(\w+)(?::([^}]*))?\}")]
    private static partial Regex PlaceholderRx();

    public static string Help => T("{path} caminho completo · {dir} pasta · {name} nome sem extensão · {filename} nome com extensão · {ext} extensão · {paths} todos os caminhos entre aspas · {out:mp4} arquivo de saída novo · {ask:Pergunta} pede um valor ao rodar · {date} data (aaaa-mm-dd) · {env:VAR} variável de ambiente");

    /// <summary>Questions in the arguments, e.g. {ask:Largura}, in order of appearance.</summary>
    public static IReadOnlyList<string> Questions(CustomAction a) =>
        PlaceholderRx().Matches(a.Arguments + " " + a.Command + " " + a.WorkingDirectory)
            .Where(m => m.Groups[1].Value.Equals("ask", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[2].Value)
            .Distinct()
            .ToList();

    public static string Expand(string template, string file, IReadOnlyList<string> all, IReadOnlyDictionary<string, string> answers, List<string> outputs)
    {
        return PlaceholderRx().Replace(template, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var arg = m.Groups[2].Success ? m.Groups[2].Value : null;
            return key switch
            {
                "path" => file,
                "dir" => Path.GetDirectoryName(file.TrimEnd('\\')) ?? "",
                "name" => OutputPaths.StemOf(file),
                "filename" => Path.GetFileName(file.TrimEnd('\\')),
                "ext" => Formats.Ext(file),
                "paths" => string.Join(" ", all.Select(p => $"\"{p}\"")),
                "out" => AddOut(file, arg, outputs),
                "ask" => answers.TryGetValue(arg ?? "", out var v) ? v : "",
                "date" => DateTime.Now.ToString("yyyy-MM-dd"),
                "time" => DateTime.Now.ToString("HH-mm-ss"),
                "env" => Environment.GetEnvironmentVariable(arg ?? "") ?? "",
                _ => m.Value,
            };
        });
    }

    private static string AddOut(string file, string? ext, List<string> outputs)
    {
        var e = string.IsNullOrWhiteSpace(ext) ? Formats.Ext(file) : ext.Trim().TrimStart('.');
        var path = OutputPaths.For(file, e, "saída"); // suffix is translated by OutputPaths
        // Reserve the name so a second {out} in the same run gets another one.
        if (!outputs.Contains(path)) outputs.Add(path);
        return path;
    }

    /// <summary>Runs the action and returns files it produced through {out:...}.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(CustomAction a, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> answers, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(a.Command)) throw new InvalidOperationException(T("A ação não tem comando."));
        var produced = new List<string>();
        var batches = a.Mode == CustomRunMode.AllAtOnce ? [files[0]] : files.ToList();
        var errors = new StringBuilder();
        for (var i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = batches[i];
            var outs = new List<string>();
            var args = Expand(a.Arguments ?? "", file, files, answers, outs);
            var cmd = Expand(a.Command, file, files, answers, outs);
            var wd = string.IsNullOrWhiteSpace(a.WorkingDirectory)
                ? (Directory.Exists(file) ? file : Path.GetDirectoryName(file))
                : Expand(a.WorkingDirectory!, file, files, answers, outs);

            var psi = new ProcessStartInfo(cmd, args)
            {
                // Shell execute resolves PATH, .cmd/.bat and file associations, like the Run dialog.
                UseShellExecute = true,
                WorkingDirectory = wd ?? "",
                WindowStyle = a.ShowWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            };
            Process? p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(T("Não foi possível iniciar \"{0}\": {1}", cmd, ex.Message), ex);
            }

            if (a.WaitForExit && p is not null)
            {
                using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                {
                    await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                if (p.ExitCode != 0) errors.AppendLine(T("{0}: código de saída {1}", Path.GetFileName(file), p.ExitCode));
            }
            p?.Dispose();
            produced.AddRange(outs.Where(File.Exists));
            progress?.Report((i + 1) / (double)batches.Count);
        }
        if (errors.Length > 0 && produced.Count == 0) throw new InvalidOperationException(errors.ToString().Trim());
        return produced;
    }
}
