// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinKame.Core;
using WinKame.Core.Engines;
using WinKame.Input;
using WinKame.Interop;
using WinKame.Ipc;
using WinKame.Jobs;
using WinKame.Settings;
using WinKame.Shelf;
using WinKame.Tray;
using WinKame.Wheel;

namespace WinKame;

public partial class App : Application
{
    public static App Instance => (App)Current;

    public AppSettings Settings { get; private set; } = new();

    private DragWatcher? _watcher;
    private TrayIcon? _tray;
    private JobsWindow? _jobs;
    private ShelfWindow? _shelf;
    private WheelWindow? _wheel;
    private SettingsWindow? _settingsWindow;
    private bool _paused;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _pendingFiles = [];
    private DispatcherTimer? _batch;
    private WheelKind? _pendingKind;

    public ShelfWindow Shelf => _shelf ??= new ShelfWindow();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0].Equals("--convert", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunCommandLineAsync(e.Args);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0].Equals("--snapshot", StringComparison.OrdinalIgnoreCase))
        {
            Settings = AppSettings.Load();
            L.Use(Settings.Language);
            Accent.Apply(Settings.AccentColor);
            ImageEngine.Init();
            _ = Snapshot.RunAsync(e.Args, Settings).ContinueWith(t =>
            {
                if (t.Exception is { } ex) Console.Error.WriteLine(ex.GetBaseException());
                Dispatcher.Invoke(() => Shutdown(t.IsFaulted ? 1 : 0));
            });
            return;
        }

        if (!SingleInstance.TryBecomePrimary())
        {
            // Another WinKame is running: hand it our arguments (e.g. files from Explorer's menu).
            SingleInstance.SendToPrimary(e.Args.Length == 0 ? ["--settings"] : e.Args);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            Ui.Error(T("Erro inesperado: {0}", ex.Exception.Message));
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log(ex.Exception);
            ex.SetObserved();
        };

        ImageEngine.Init();
        Settings = AppSettings.Load();
        L.Use(Settings.Language);
        Settings.ApplyToEngines();
        Accent.Apply(Settings.AccentColor);
        Accent.WatchWindows();
        Pdf.WebViewPrinter.Install();
        JobRunner.RevealWhenDone = Settings.RevealWhenDone;

        _jobs = new JobsWindow();
        JobRunner.Started += _ => _jobs.Reveal();
        JobRunner.Completed += _ => _jobs.Reveal();

        _tray = new TrayIcon();
        _tray.SettingsRequested += () => OpenSettings();
        _tray.ShelfRequested += () =>
        {
            Shelf.AddFiles([]);
            Shelf.Show();
            Shelf.Activate();
        };
        _tray.JobsRequested += () => _jobs.Reveal();
        _tray.PauseToggled += p =>
        {
            _paused = p;
            if (_watcher is not null) _watcher.Paused = p;
        };
        _tray.ExitRequested += Quit;

        _watcher = new DragWatcher(Dispatcher);
        ApplyWatcherSettings();
        _watcher.Tapped += OnTap;
        _watcher.DragEnded += OnDragEnded;
        _watcher.Start();
        if (!_watcher.HooksInstalled)
        {
            Ui.Error(T("Não foi possível monitorar o mouse e o teclado, então a roda por arrasto não vai abrir. O menu do Explorer continua funcionando."));
        }

        _batch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _batch.Tick += (_, _) => FlushPending();
        SingleInstance.Listen(HandleArgs, _cts.Token);

        HandleArgs(e.Args);

        if (Settings.ShowWelcome)
        {
            _tray.Balloon(T("WinKame está rodando"),
                T("Comece a arrastar um arquivo e toque {0} para converter, {1} para ferramentas ou {2} para ações.",
                    DragWatcher.KeyLabel(Settings.KeyConvert), DragWatcher.KeyLabel(Settings.KeyTools), DragWatcher.KeyLabel(Settings.KeyActions)));
            Settings.ShowWelcome = false;
            TrySave(Settings);
        }
    }

    /// <summary>
    /// Headless conversion: WinKame.exe --convert file1 [file2…] --to pdf
    /// Prints one line per file ("OK path" or "ERRO file: message") and exits with 0 when all succeed.
    /// </summary>
    private async Task RunCommandLineAsync(string[] args)
    {
        var output = Console.Out;
        var exit = 0;
        try
        {
            var toIndex = Array.FindIndex(args, a => a.Equals("--to", StringComparison.OrdinalIgnoreCase));
            if (toIndex < 0 || toIndex == args.Length - 1)
            {
                await output.WriteLineAsync(T("Uso: WinKame.exe --convert <arquivos…> --to <formato>"));
                exit = 2;
                return;
            }
            var target = args[toIndex + 1];
            var files = args.Skip(1).Where((_, i) => i + 1 != toIndex && i + 1 != toIndex + 1).ToList();

            ImageEngine.Init();
            Settings = AppSettings.Load();
            L.Use(Settings.Language);
            Settings.ApplyToEngines();
            Pdf.WebViewPrinter.Install();

            foreach (var file in files)
            {
                try
                {
                    var outs = await Converter.ConvertAsync(Path.GetFullPath(file), target, null, CancellationToken.None);
                    await output.WriteLineAsync("OK " + string.Join(" | ", outs));
                }
                catch (Exception ex)
                {
                    await output.WriteLineAsync($"{T("ERRO")} {file}: {ex.Message}");
                    exit = 1;
                }
            }
        }
        finally
        {
            await output.FlushAsync();
            Shutdown(exit);
        }
    }

    private void ApplyWatcherSettings()
    {
        if (_watcher is null) return;
        _watcher.SetKeys(Settings.KeyConvert, Settings.KeyTools, Settings.KeyActions, Settings.KeyCustom);
        _watcher.TapMaxMs = Settings.TapMaxMs;
        _watcher.ThresholdPx = Settings.DragThresholdPx;
        _watcher.Paused = _paused || !Settings.DragWheelEnabled;
    }

    // ---------- arguments from the command line, Explorer's menu or "Send to" ----------

    private void HandleArgs(string[] args)
    {
        Debug("args: " + string.Join(" | ", args));
        if (args.Length == 0) return;
        var flag = args[0].ToLowerInvariant();
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        // Optional "--kind=tools" picks which wheel opens (handy for testing and shortcuts).
        var kindArg = args.FirstOrDefault(a => a.StartsWith("--kind=", StringComparison.OrdinalIgnoreCase))?[7..];
        if (Enum.TryParse<WheelKind>(kindArg, true, out var k)) _pendingKind = k;
        switch (flag)
        {
            case "--wheel":
                _pendingFiles.AddRange(paths);
                // Explorer starts one process per selected file; collect them for a moment.
                _batch?.Stop();
                _batch?.Start();
                break;
            case "--shelf":
                Shelf.AddFiles(paths);
                Shelf.Show();
                break;
            case "--settings":
                OpenSettings(args.Length > 1 ? args[1] : null);
                break;
            case "--background":
                break;
            case "--quit":
                Quit();
                break;
            default:
                // Files dropped on the exe or passed directly.
                if (paths.Count > 0)
                {
                    _pendingFiles.AddRange(paths);
                    _batch?.Stop();
                    _batch?.Start();
                }
                break;
        }
    }

    private void FlushPending()
    {
        _batch?.Stop();
        var files = _pendingFiles
            .Select(p => p.Trim('"'))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _pendingFiles.Clear();
        var kind = _pendingKind;
        _pendingKind = null;
        if (files.Count == 0) return;
        ShowClickWheel(files, kind);
    }

    public void ShowClickWheel(IReadOnlyList<string> files, WheelKind? kind = null)
    {
        _wheel?.Dismiss();
        var k = kind ?? (Formats.CommonTargets(files).Count > 0 ? WheelKind.Convert : Formats.CommonKind(files) is FileKind.Image or FileKind.Video or FileKind.Audio or FileKind.Pdf or FileKind.Archive ? WheelKind.Tools : WheelKind.Actions);
        var wheel = new WheelWindow(k, Native.CursorPos(), files, clickMode: true, Settings);
        wheel.Finished += () => { if (_wheel == wheel) _wheel = null; };
        _wheel = wheel;
        wheel.ShowForClick();
    }

    // ---------- drag wheel ----------

    private void OnTap(WheelKind kind, Native.POINT pt)
    {
        Debug($"tap {kind} at {pt.X},{pt.Y}");
        if (_paused || !Settings.DragWheelEnabled) return;
        if (_wheel is { IsClickMode: false, IsDone: false } open)
        {
            open.SwitchKind(kind);
            return;
        }
        _wheel?.Dismiss();
        var wheel = new WheelWindow(kind, pt, null, clickMode: false, Settings);
        wheel.Finished += () => { if (_wheel == wheel) _wheel = null; };
        _wheel = wheel;
        wheel.ShowForDrag();
    }

    private void OnDragEnded()
    {
        if (_wheel is { IsClickMode: false } w) w.OnDragEnded();
    }

    // ---------- windows ----------

    public void OpenSettings(string? tab = null)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var win = new SettingsWindow(Settings, tab);
        win.Saved += s =>
        {
            Settings = s;
            TrySave(s);
            s.ApplyToEngines();
            Accent.Apply(s.AccentColor);
            ApplyWatcherSettings();
            JobRunner.RevealWhenDone = s.RevealWhenDone;
        };
        win.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = win;
        Ui.CenterOnCursorMonitor(win);
        win.Show();
        Native.ForceForeground(new WindowInteropHelper(win).Handle);
        win.Activate();
    }

    /// <summary>A short message in the job card, for actions that finish instantly.</summary>
    public void Toast(string title, string detail)
    {
        var job = new Job { Title = T(title) };
        job.Detail = T(detail);
        job.Progress = 1;
        job.State = JobState.Done;
        JobRunner.All.Insert(0, job);
        _jobs?.Reveal();
    }

    private static void TrySave(AppSettings s)
    {
        try
        {
            s.Save();
        }
        catch (Exception ex)
        {
            Ui.Error(T("Não foi possível salvar as configurações: {0}", ex.Message));
        }
    }

    private static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("WINKAME_DEBUG") == "1";

    /// <summary>Event trace written only when WINKAME_DEBUG=1, for diagnosing drag and focus issues.</summary>
    public static void Debug(string message)
    {
        if (!DebugEnabled) return;
        try
        {
            File.AppendAllText(Path.Combine(AppSettings.Folder, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // best effort
        }
    }

    public static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(Path.Combine(AppSettings.Folder, "erros.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // nowhere else to report it
        }
    }

    private void Quit()
    {
        foreach (var j in JobRunner.All.Where(j => j.IsRunning)) j.Cts.Cancel();
        _cts.Cancel();
        _watcher?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
