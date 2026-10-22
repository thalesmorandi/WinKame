using System.Collections.Specialized;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinKame.Interop;
using WinKame.Shell;

namespace WinKame.Jobs;

/// <summary>Bottom-right card listing running and recent jobs. Never steals focus.</summary>
public partial class JobsWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public JobsWindow()
    {
        InitializeComponent();
        List.ItemsSource = JobRunner.All;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        SizeChanged += (_, _) => PlaceInCorner();
        JobRunner.All.CollectionChanged += OnJobsChanged;
        _hideTimer.Tick += (_, _) => MaybeHide();
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Keep the card short.
        while (JobRunner.All.Count > 5)
        {
            var old = JobRunner.All.LastOrDefault(j => j.IsFinished);
            if (old is null) break;
            JobRunner.All.Remove(old);
        }
        if (JobRunner.All.Count == 0) Hide();
    }

    public void Reveal()
    {
        if (!IsVisible) Show();
        PlaceInCorner();
        _hideTimer.Start();
    }

    private void PlaceInCorner()
    {
        var (work, scale) = Native.MonitorAt(Native.CursorPos());
        Left = work.Right / scale - ActualWidth - 4;
        Top = work.Bottom / scale - ActualHeight - 4;
    }

    private void MaybeHide()
    {
        if (IsMouseOver) return;
        if (JobRunner.All.Any(j => j.IsRunning)) return;
        var lastFinish = JobRunner.All.Select(j => j.Finished).DefaultIfEmpty(DateTime.MinValue).Max();
        var hasErrors = JobRunner.All.Any(j => j.State == JobState.Failed && DateTime.Now - j.Finished < TimeSpan.FromSeconds(20));
        var wait = hasErrors ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(6);
        if (DateTime.Now - lastFinish > wait)
        {
            Hide();
            _hideTimer.Stop();
        }
    }

    private static Job? JobOf(object sender) => (sender as FrameworkElement)?.DataContext as Job;

    private void Cancel_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Cts.Cancel();

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } j) FileOps.Reveal(j.Outputs);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { MissingTool: { } tool }) await ToolGate.InstallAsync(tool);
    }

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { Error: { } err })
        {
            try { Clipboard.SetText(err); } catch { /* clipboard busy */ }
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        JobRunner.ClearFinished();
        if (JobRunner.All.Count == 0) Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
