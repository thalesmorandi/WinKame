using System.Runtime.InteropServices;

namespace WinKame.Core.Engines;

public enum OfficeApp { Word, Excel, PowerPoint }

/// <summary>
/// Uses Microsoft Office through COM automation when it is installed, for the best
/// fidelity (DOCX/PPTX/XLSX to PDF, legacy DOC/PPT/XLS and OpenDocument files).
/// </summary>
public static class OfficeEngine
{
    private static readonly Dictionary<OfficeApp, bool> Available = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Lets tests and settings turn Office off to exercise the native paths.</summary>
    public static bool Disabled { get; set; } = Environment.GetEnvironmentVariable("WINKAME_NO_OFFICE") == "1";

    private static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    public static bool IsInstalled(OfficeApp app)
    {
        if (Disabled) return false;
        lock (Available)
        {
            if (!Available.TryGetValue(app, out var ok))
            {
                ok = Type.GetTypeFromProgID(ProgId(app)) is not null;
                Available[app] = ok;
            }
            return ok;
        }
    }

    // Office "save as" codes.
    private const int WdFormatPdf = 17, WdFormatDocx = 16, WdFormatRtf = 6, WdFormatOdt = 23;
    private const int XlPdf = 0, XlXlsx = 51, XlOds = 60;
    private const int PpPdf = 32, PpPptx = 24, PpOdp = 35;

    /// <summary>Which Office app can produce <paramref name="target"/> from <paramref name="input"/>, if any.</summary>
    public static OfficeApp? AppFor(string input, string target)
    {
        var src = Formats.Canonical(Formats.Ext(input));
        target = Formats.Canonical(target);
        OfficeApp? app = src switch
        {
            "docx" or "doc" or "rtf" or "odt" when target is "pdf" or "docx" or "rtf" or "odt" => OfficeApp.Word,
            "xlsx" or "xls" or "ods" or "csv" when target is "pdf" or "xlsx" or "ods" => OfficeApp.Excel,
            "pptx" or "ppt" or "odp" when target is "pdf" or "pptx" or "odp" => OfficeApp.PowerPoint,
            _ => null,
        };
        return app is { } a && IsInstalled(a) ? a : null;
    }

    /// <summary>Converts with Office and returns the produced file in the output folder.</summary>
    public static async Task<string> ConvertAsync(string input, string target, CancellationToken ct, string? outPath = null)
    {
        target = Formats.Canonical(target);
        var app = AppFor(input, target) ?? throw new InvalidOperationException(T("O Microsoft Office não está disponível para essa conversão."));
        var full = Path.GetFullPath(input);
        outPath ??= OutputPaths.For(input, target);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        var processName = app switch { OfficeApp.Word => "WINWORD", OfficeApp.Excel => "EXCEL", _ => "POWERPNT" };
        var before = Pids(processName);
        try
        {
            var work = RunSta(() =>
            {
                switch (app)
                {
                    case OfficeApp.Word: Word(full, target, outPath); break;
                    case OfficeApp.Excel: Excel(full, target, outPath); break;
                    case OfficeApp.PowerPoint: PowerPoint(full, target, outPath); break;
                }
            }, ct);
            if (await Task.WhenAny(work, Task.Delay(Timeout, ct)).ConfigureAwait(false) != work)
            {
                // Office is waiting on something we can't see (sensitivity label, activation, a prompt).
                // Close only the instance we started, never the user's own windows.
                foreach (var pid in Pids(processName).Except(before))
                {
                    try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
                }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(T("O Office não respondeu a tempo (pode estar esperando uma janela escondida, como um rótulo de confidencialidade)."));
            }
            await work.ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
        if (!File.Exists(outPath)) throw new InvalidOperationException(T("O Office não gerou o arquivo."));
        return outPath;
    }

    private static readonly HashSet<(OfficeApp, string)> Failed = new();

    /// <summary>Last reason Office could not be used (for the job card), or null.</summary>
    public static string? LastFailure { get; private set; }

    /// <summary>
    /// Office if it works, otherwise null so the caller falls back to the native engine or LibreOffice.
    /// A combination that failed once is skipped for the rest of the session.
    /// </summary>
    public static async Task<string?> TryConvertAsync(string input, string target, CancellationToken ct, string? outPath = null)
    {
        target = Formats.Canonical(target);
        if (AppFor(input, target) is not { } app) return null;
        lock (Failed)
        {
            if (Failed.Contains((app, target))) return null;
        }
        try
        {
            return await ConvertAsync(input, target, ct, outPath).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastFailure = ex.Message;
            lock (Failed) Failed.Add((app, target));
            if (outPath is not null) OutputPaths.TryDelete(outPath);
            return null;
        }
    }

    /// <summary>How long one Office conversion may take before we give up and use another engine.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    private static HashSet<int> Pids(string name) =>
        System.Diagnostics.Process.GetProcessesByName(name).Select(p => p.Id).ToHashSet();

    private static Task RunSta(Action action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                action();
                // Intermediate COM objects (Documents, Workbooks…) keep Office alive until collected.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex is COMException ce ? new InvalidOperationException(T("O Office recusou o arquivo: {0}", ce.Message), ce) : ex);
            }
        })
        {
            IsBackground = true,
            Name = "WinKame.Office",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private static dynamic Create(OfficeApp app) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(ProgId(app))!)!;

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }

    private static void Word(string input, string target, string outPath)
    {
        dynamic? word = null, doc = null;
        try
        {
            word = Create(OfficeApp.Word);
            word.Visible = false;
            word.DisplayAlerts = 0;
            doc = word.Documents.Open(input, false, true, false); // ConfirmConversions, ReadOnly, AddToRecentFiles
            int fmt = target switch { "pdf" => WdFormatPdf, "rtf" => WdFormatRtf, "odt" => WdFormatOdt, _ => WdFormatDocx };
            doc.SaveAs2(outPath, fmt);
        }
        finally
        {
            try { doc?.Close(0); } catch { }
            try { word?.Quit(0); } catch { }
            Release(doc);
            Release(word);
        }
    }

    private static void Excel(string input, string target, string outPath)
    {
        dynamic? excel = null, book = null;
        try
        {
            excel = Create(OfficeApp.Excel);
            excel.Visible = false;
            excel.DisplayAlerts = false;
            book = excel.Workbooks.Open(input, 0, true); // UpdateLinks, ReadOnly
            if (target == "pdf") book.ExportAsFixedFormat(XlPdf, outPath);
            else book.SaveAs(outPath, target == "ods" ? XlOds : XlXlsx);
        }
        finally
        {
            try { book?.Close(false); } catch { }
            try { excel?.Quit(); } catch { }
            Release(book);
            Release(excel);
        }
    }

    private static void PowerPoint(string input, string target, string outPath)
    {
        dynamic? ppt = null, pres = null;
        try
        {
            ppt = Create(OfficeApp.PowerPoint);
            pres = ppt.Presentations.Open(input, -1, 0, 0); // ReadOnly, Untitled, WithWindow
            int fmt = target switch { "pdf" => PpPdf, "odp" => PpOdp, _ => PpPptx };
            pres.SaveAs(outPath, fmt);
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { ppt?.Quit(); } catch { }
            Release(pres);
            Release(ppt);
        }
    }
}
