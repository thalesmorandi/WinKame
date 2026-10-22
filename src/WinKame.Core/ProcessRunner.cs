using System.Diagnostics;
using System.Text;

namespace WinKame.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public sealed class ToolFailedException(string tool, int exitCode, string detail)
    : Exception(T("{0} falhou (código {1}).", tool, exitCode) + "\n" + detail)
{
    public string Tool { get; } = tool;
    public int ExitCode { get; } = exitCode;
    public string Detail { get; } = detail;
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        CancellationToken ct,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        string? workDir = null,
        bool throwOnError = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (so) so.AppendLine(e.Data);
            onStdOut?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (se)
            {
                // Keep memory bounded for chatty tools like ffmpeg.
                if (se.Length > 200_000) se.Remove(0, 100_000);
                se.AppendLine(e.Data);
            }
            onStdErr?.Invoke(e.Data);
        };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }))
        {
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        p.WaitForExit(); // flush the async readers

        ct.ThrowIfCancellationRequested();
        var result = new ProcessResult(p.ExitCode, so.ToString(), se.ToString());
        if (throwOnError && p.ExitCode != 0)
        {
            throw new ToolFailedException(Path.GetFileNameWithoutExtension(exe), p.ExitCode, Tail(result.StdErr.Length > 0 ? result.StdErr : result.StdOut));
        }
        return result;
    }

    public static string Tail(string text, int lines = 12)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }
}
