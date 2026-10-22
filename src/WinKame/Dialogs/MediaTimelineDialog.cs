// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinKame.Core.Engines;
using WpfImage = System.Windows.Controls.Image;

namespace WinKame.Dialogs;

/// <summary>
/// A timeline with a preview (video frame or audio waveform) to pick a range (trim) or a list
/// of points (split, snapshots).
/// </summary>
public sealed class MediaTimelineDialog : Window
{
    public enum Mode { Range, Points }

    private readonly string _file;
    private readonly Mode _mode;
    private readonly MediaInfo _info;
    private readonly bool _video;
    private readonly WpfImage _preview = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _bar = new() { Height = 26, ClipToBounds = true };
    private readonly Slider _slider = new();
    private readonly TextBlock _pos = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _start = new() { Width = 90 }, _end = new() { Width = 90 };
    private readonly ListBox _points = new() { Height = 120 };
    private readonly CheckBox _silence = new() { Content = T("Remover silêncio no início e no fim"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private CancellationTokenSource? _frameCts;
    private readonly List<TimeSpan> _pointList = [];

    private MediaTimelineDialog(string file, Mode mode, MediaInfo info, byte[]? waveform, string title, string ok)
    {
        Ui.Themed(this);
        title = T(title);
        ok = T(ok);
        _file = file;
        _mode = mode;
        _info = info;
        _video = info.HasVideo;
        Title = title;
        Width = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var total = info.Duration.TotalSeconds;
        _slider.Minimum = 0;
        _slider.Maximum = Math.Max(0.1, total);
        _slider.SmallChange = 0.1;
        _slider.LargeChange = Math.Max(1, total / 20);
        _slider.ValueChanged += (_, _) => OnPosition();

        var previewHost = new Grid { Height = _video ? 340 : 140, Margin = new Thickness(0, 12, 0, 8) };
        previewHost.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)) });
        if (!_video && waveform is not null) _preview.Source = Ui.ToBitmap(waveform);
        _preview.Stretch = _video ? Stretch.Uniform : Stretch.Fill;
        previewHost.Children.Add(_preview);

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = $"{System.IO.Path.GetFileName(file)} · {MediaEngine.FormatTime(info.Duration)}", Opacity = 0.65 });
        root.Children.Add(previewHost);
        root.Children.Add(_bar);
        root.Children.Add(_slider);

        var posRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        posRow.Children.Add(_pos);
        root.Children.Add(posRow);

        if (mode == Mode.Range)
        {
            _start.Text = MediaEngine.FormatTime(TimeSpan.Zero);
            _end.Text = MediaEngine.FormatTime(info.Duration);
            _start.TextChanged += (_, _) => DrawBar();
            _end.TextChanged += (_, _) => DrawBar();
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            row.Children.Add(Labeled(T("Início"), _start));
            row.Children.Add(Btn(T("Marcar início aqui"), () => _start.Text = MediaEngine.FormatTime(Now)));
            row.Children.Add(new Border { Width = 18 });
            row.Children.Add(Labeled(T("Fim"), _end));
            row.Children.Add(Btn(T("Marcar fim aqui"), () => _end.Text = MediaEngine.FormatTime(Now)));
            root.Children.Add(row);
            if (!_video) root.Children.Add(_silence);
        }
        else
        {
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
            row.Children.Add(Btn(T("Adicionar ponto aqui"), () => AddPoint(Now)));
            row.Children.Add(Btn(T("Remover selecionado"), () =>
            {
                if (_points.SelectedIndex >= 0) { _pointList.RemoveAt(_points.SelectedIndex); RefreshPoints(); }
            }));
            var every = new TextBox { Width = 60, Text = "30", Margin = new Thickness(16, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = T("ou a cada"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            row.Children.Add(every);
            row.Children.Add(new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Btn(T("Gerar"), () =>
            {
                if (!MediaEngine.TryParseTime(every.Text, out var step) || step.TotalSeconds < 0.5) return;
                _pointList.Clear();
                for (var t = step; t < info.Duration; t += step) _pointList.Add(t);
                RefreshPoints();
            }));
            root.Children.Add(row);
            root.Children.Add(_points);
        }

        var okBtn = new Button { Content = ok, IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent) okBtn.Style = accent;
        okBtn.Click += (_, _) =>
        {
            if (_mode == Mode.Points && _pointList.Count == 0) AddPoint(Now);
            if (_mode == Mode.Range && (!TryRange(out var s, out var e) || e <= s)) return;
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button { Content = T("Cancelar"), IsCancel = true, MinWidth = 90 });
        buttons.Children.Add(okBtn);
        root.Children.Add(buttons);
        Content = root;

        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = LoadFrame(); };
        Loaded += (_, _) => { OnPosition(); DrawBar(); };
        _bar.SizeChanged += (_, _) => DrawBar();
    }

    private TimeSpan Now => TimeSpan.FromSeconds(_slider.Value);

    private static FrameworkElement Labeled(string label, Control c)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        p.Children.Add(c);
        return p;
    }

    private static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    private void AddPoint(TimeSpan t)
    {
        if (t <= TimeSpan.Zero || t >= _info.Duration || _pointList.Any(p => Math.Abs((p - t).TotalSeconds) < 0.05)) return;
        _pointList.Add(t);
        _pointList.Sort();
        RefreshPoints();
    }

    private void RefreshPoints()
    {
        _points.ItemsSource = null;
        _points.ItemsSource = _pointList.Select(p => MediaEngine.FormatTime(p)).ToList();
        DrawBar();
    }

    private bool TryRange(out TimeSpan s, out TimeSpan e)
    {
        var ok1 = MediaEngine.TryParseTime(_start.Text, out s);
        var ok2 = MediaEngine.TryParseTime(_end.Text, out e);
        return ok1 && ok2;
    }

    private void OnPosition()
    {
        _pos.Text = MediaEngine.FormatTime(Now);
        DrawBar();
        if (_video)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private async Task LoadFrame()
    {
        _frameCts?.Cancel();
        var cts = _frameCts = new CancellationTokenSource();
        try
        {
            var t = Now;
            var png = await Task.Run(() => MediaEngine.FrameAsync(_file, t, 720, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) _preview.Source = Ui.ToBitmap(png);
        }
        catch
        {
            // a frame we can't grab just leaves the previous one
        }
    }

    private void DrawBar()
    {
        _bar.Children.Clear();
        var w = _bar.ActualWidth;
        if (w <= 0) return;
        var total = Math.Max(0.001, _info.Duration.TotalSeconds);
        double X(TimeSpan t) => Math.Clamp(t.TotalSeconds / total, 0, 1) * w;
        _bar.Children.Add(new Rectangle { Width = w, Height = 6, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) });
        Canvas.SetTop(_bar.Children[^1], 10);
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F));
        if (_mode == Mode.Range && TryRange(out var s, out var e) && e > s)
        {
            var r = new Rectangle { Width = Math.Max(2, X(e) - X(s)), Height = 6, RadiusX = 3, RadiusY = 3, Fill = accent };
            Canvas.SetLeft(r, X(s));
            Canvas.SetTop(r, 10);
            _bar.Children.Add(r);
        }
        foreach (var p in _pointList)
        {
            var m = new Rectangle { Width = 3, Height = 18, Fill = accent };
            Canvas.SetLeft(m, X(p) - 1.5);
            Canvas.SetTop(m, 4);
            _bar.Children.Add(m);
        }
        var cursor = new Rectangle { Width = 2, Height = 26, Fill = Brushes.White, Opacity = 0.9 };
        Canvas.SetLeft(cursor, X(Now) - 1);
        _bar.Children.Add(cursor);
    }

    private static async Task<(MediaInfo Info, byte[]? Wave)> Prepare(string file)
    {
        var info = await MediaEngine.ProbeAsync(file);
        byte[]? wave = null;
        if (!info.HasVideo)
        {
            try { wave = await MediaEngine.WaveformAsync(file, 1400, 240, "#FF8A1F", default); }
            catch { /* the slider still works without it */ }
        }
        return (info, wave);
    }

    public static async Task<(TimeSpan Start, TimeSpan End, bool TrimSilence)?> PickRange(string file, string title, string ok)
    {
        var (info, wave) = await Prepare(file);
        var dlg = new MediaTimelineDialog(file, Mode.Range, info, wave, title, ok);
        if (Ui.ShowOnTop(dlg) != true || !dlg.TryRange(out var s, out var e)) return null;
        return (s, e, dlg._silence.IsChecked == true);
    }

    public static async Task<IReadOnlyList<TimeSpan>?> PickPoints(string file, string title, string ok)
    {
        var (info, wave) = await Prepare(file);
        var dlg = new MediaTimelineDialog(file, Mode.Points, info, wave, title, ok);
        if (Ui.ShowOnTop(dlg) != true) return null;
        return dlg._pointList.ToList();
    }
}
