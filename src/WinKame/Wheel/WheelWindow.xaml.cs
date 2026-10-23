// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinKame.Actions;
using WinKame.Core;
using WinKame.Input;
using WinKame.Interop;
using WinKame.Shell;
using WpfImage = System.Windows.Controls.Image;
using WpfPath = System.Windows.Shapes.Path;

namespace WinKame.Wheel;

/// <summary>
/// The radial menu. In drag mode it appears under the cursor without taking focus and receives
/// the drop; in click mode (opened from Explorer's menu) it behaves like a normal popup.
/// </summary>
public partial class WheelWindow : Window
{
    private const double OuterR = 152;
    private const double InnerR = 66;
    private const double Pad = 26;
    private const double PillSpace = 50;
    private const double GapDeg = 1.2;

    private static double Side => 2 * (OuterR + Pad);

    // Wider than the ring so the key chips below it fit.
    private static double WinW => Side + 120;
    private static Point Center => new(WinW / 2, OuterR + Pad);

    private readonly bool _clickMode;
    private readonly AppSettings _settings;
    private readonly Native.POINT _anchor;
    private WheelKind _kind;
    private IReadOnlyList<string>? _files;
    private IReadOnlyList<WheelItem> _items = [];
    private int _hover = -1;
    private bool _done;
    private bool _revealed;
    private IntPtr _hwnd;
    private long _shownAt;

    private readonly List<WpfPath> _slices = [];
    private readonly List<FrameworkElement> _labels = [];
    private TextBlock _centerTitle = null!, _centerText = null!;
    private WpfImage _centerIcon = null!;

    private static readonly Brush SliceFill = Brushes.Transparent;
    private readonly Brush _sliceHover = Accent.SliceHoverBrush();
    private static readonly Brush Divider = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    public event Action? Finished;

    public WheelWindow(WheelKind kind, Native.POINT anchor, IReadOnlyList<string>? files, bool clickMode, AppSettings settings)
    {
        InitializeComponent();
        _kind = kind;
        _anchor = anchor;
        _clickMode = clickMode;
        _settings = settings;
        Width = WinW;
        Height = Side + PillSpace;
        Left = -30000;
        Top = -30000;
        ShowActivated = clickMode;

        var scale = Math.Clamp(settings.WheelScale, 0.7, 1.6);
        if (Math.Abs(scale - 1) > 0.01)
        {
            Root.LayoutTransform = new ScaleTransform(scale, scale);
            Width *= scale;
            Height *= scale;
        }

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | (clickMode ? 0 : Native.WS_EX_NOACTIVATE));
        };
        Loaded += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Render);

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHover(-1);
        Drop += OnDrop;

        if (clickMode)
        {
            MouseMove += (_, e) => SetHover(HitIndex(e.GetPosition(Root)));
            MouseLeave += (_, _) => SetHover(-1);
            MouseLeftButtonUp += OnClick;
            KeyDown += OnKey;
            Deactivated += (_, _) =>
            {
                App.Debug($"wheel deactivated after {Environment.TickCount64 - _shownAt} ms");
                if (_done) return;
                // Right after opening, another window may briefly take focus back: reclaim it instead of closing.
                if (Environment.TickCount64 - _shownAt < 1500)
                {
                    Dispatcher.BeginInvoke(() => { Native.ForceForeground(_hwnd); Activate(); }, DispatcherPriority.Background);
                    return;
                }
                Dismiss();
            };
        }

        BuildStatic();
        if (files is not null) SetFiles(files);
    }

    // ---------- placement ----------

    private void Place()
    {
        if (_hwnd == IntPtr.Zero || SnapshotMode) return;
        var (work, scale) = Native.MonitorAt(_anchor);
        var factor = scale * (Root.LayoutTransform is ScaleTransform st ? st.ScaleX : 1);
        var w = (int)Math.Round(WinW * factor);
        var h = (int)Math.Round((Side + PillSpace) * factor);
        var x = _anchor.X - (int)Math.Round(Center.X * factor);
        var y = _anchor.Y - (int)Math.Round(Center.Y * factor);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    }

    /// <summary>Shows the (still invisible) catcher and nudges the cursor so the drag loop notices us.</summary>
    public void ShowForDrag()
    {
        _shownAt = Environment.TickCount64;
        App.Debug($"wheel drag-mode at {_anchor.X},{_anchor.Y} kind={_kind}");
        Show();
        Place();
        var p = Native.CursorPos();
        Native.SetCursorPos(p.X + 1, p.Y);
        Native.SetCursorPos(p.X, p.Y);
        // Not a file drag after all (text selection, window move…): go away quietly.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_files is null && !_done)
            {
                App.Debug("wheel: no DragEnter, not a file drag");
                Dismiss();
            }
        };
        timer.Start();

        // Safety net: if the button is up and nothing was dropped on us, the drag is over.
        var upTicks = 0;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        watch.Tick += (_, _) =>
        {
            if (_done)
            {
                watch.Stop();
                return;
            }
            var down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
            upTicks = down ? 0 : upTicks + 1;
            if (upTicks >= 3)
            {
                watch.Stop();
                App.Debug("wheel: mouse button up without a drop");
                Dismiss();
            }
        };
        watch.Start();
    }

    public void ShowForClick()
    {
        _shownAt = Environment.TickCount64;
        App.Debug($"wheel click-mode at {_anchor.X},{_anchor.Y} kind={_kind} files={_files?.Count}");
        Show();
        Place();
        Reveal();
        Native.ForceForeground(_hwnd);
        Activate();
        Focus();
    }

    /// <summary>For --snapshot: never placed on screen, shown fully without animation.</summary>
    internal bool SnapshotMode { get; set; }

    internal void RevealInstantly()
    {
        _revealed = true;
        Body.Opacity = 1;
        Zoom.ScaleX = Zoom.ScaleY = 1;
    }

    private void Reveal()
    {
        if (_revealed) return;
        _revealed = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
    }

    // ---------- content ----------

    private void BuildStatic()
    {
        // A soft shadow drawn with a gradient: DropShadowEffect on a large element of a
        // transparent window made the whole disc render semi-transparent.
        var shadowR = OuterR + Pad - 2;
        var shadow = new Ellipse
        {
            Width = 2 * shadowR, Height = 2 * shadowR, IsHitTestVisible = false,
            Fill = new RadialGradientBrush(
            [
                new GradientStop(Color.FromArgb(0x70, 0, 0, 0), (OuterR + 2) / shadowR),
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0),
            ]),
        };
        Canvas.SetLeft(shadow, Center.X - shadowR);
        Canvas.SetTop(shadow, Center.Y - shadowR + 3);
        Ring.Children.Add(shadow);

        var backdrop = new Ellipse
        {
            Width = 2 * (OuterR + 6), Height = 2 * (OuterR + 6),
            Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x21)), Stroke = (Brush)FindResource("StrokeBrush"), StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(backdrop, Center.X - OuterR - 6);
        Canvas.SetTop(backdrop, Center.Y - OuterR - 6);
        Ring.Children.Add(backdrop);

        var hub = new Ellipse
        {
            Width = 2 * (InnerR - 6), Height = 2 * (InnerR - 6),
            Fill = (Brush)FindResource("SurfaceRaisedBrush"), IsHitTestVisible = false,
        };
        Canvas.SetLeft(hub, Center.X - InnerR + 6);
        Canvas.SetTop(hub, Center.Y - InnerR + 6);
        Ring.Children.Add(hub);
    }

    public void SetFiles(IReadOnlyList<string> files)
    {
        _files = files;
        Rebuild();
    }

    public void SwitchKind(WheelKind kind)
    {
        App.Debug($"wheel switch to {kind}");
        if (kind == _kind && _items.Count > 0) return;
        _kind = kind;
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var s in _slices) Ring.Children.Remove(s);
        foreach (var l in _labels) Ring.Children.Remove(l);
        _slices.Clear();
        _labels.Clear();
        _hover = -1;

        _items = _files is { Count: > 0 } ? ActionCatalog.Build(_kind, _files, _settings) : [];
        var n = _items.Count;
        var span = n == 0 ? 360 : 360.0 / n;
        for (var i = 0; i < n; i++)
        {
            var slice = new WpfPath { Data = SliceGeometry(i, n), Fill = SliceFill, IsHitTestVisible = false };
            _slices.Add(slice);
            Ring.Children.Insert(2 + i, slice); // after the shadow and the backdrop
            if (n > 1)
            {
                var divider = new Line
                {
                    X1 = Center.X + InnerR * Math.Sin(Rad(i * span - span / 2)),
                    Y1 = Center.Y - InnerR * Math.Cos(Rad(i * span - span / 2)),
                    X2 = Center.X + OuterR * Math.Sin(Rad(i * span - span / 2)),
                    Y2 = Center.Y - OuterR * Math.Cos(Rad(i * span - span / 2)),
                    Stroke = Divider, StrokeThickness = 1, IsHitTestVisible = false,
                };
                _labels.Add(divider);
                Ring.Children.Add(divider);
            }
        }
        for (var i = 0; i < n; i++)
        {
            var label = Label(_items[i], n);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var mid = Rad(i * span);
            var rad = (InnerR + OuterR) / 2 + 2;
            Canvas.SetLeft(label, Center.X + rad * Math.Sin(mid) - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, Center.Y - rad * Math.Cos(mid) - label.DesiredSize.Height / 2);
            _labels.Add(label);
            Ring.Children.Add(label);
        }
        BuildCenter();
        BuildChips();
    }

    private static double Rad(double deg) => deg * Math.PI / 180;

    private static Point OnCircle(double deg, double radius) =>
        new(Center.X + radius * Math.Sin(Rad(deg)), Center.Y - radius * Math.Cos(Rad(deg)));

    private static Geometry SliceGeometry(int i, int n)
    {
        if (n == 1)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(Center, OuterR, OuterR), new EllipseGeometry(Center, InnerR, InnerR));
        }
        var span = 360.0 / n;
        var a0 = i * span - span / 2 + GapDeg / 2;
        var a1 = i * span + span / 2 - GapDeg / 2;
        var large = a1 - a0 > 180;
        var fig = new PathFigure { StartPoint = OnCircle(a0, OuterR), IsClosed = true };
        fig.Segments.Add(new ArcSegment(OnCircle(a1, OuterR), new Size(OuterR, OuterR), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(OnCircle(a1, InnerR), true));
        fig.Segments.Add(new ArcSegment(OnCircle(a0, InnerR), new Size(InnerR, InnerR), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    private FrameworkElement Label(WheelItem item, int n)
    {
        var text = (Brush)FindResource("TextBrush");
        var dim = (Brush)FindResource("TextDimBrush");
        var small = n > 10;
        var panel = new StackPanel { IsHitTestVisible = false, MaxWidth = small ? 62 : 76 };
        if (item.IsFormat)
        {
            panel.Children.Add(new TextBlock
            {
                Text = T(item.Label), Foreground = text, FontSize = item.Label.Length > 4 ? 12 : 15, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = item.Glyph, FontFamily = Ui.GlyphFont(item.Glyph), FontSize = small ? 17 : 20, Foreground = text,
                HorizontalAlignment = HorizontalAlignment.Center, FontWeight = Ui.IsIconGlyph(item.Glyph) ? FontWeights.Normal : FontWeights.SemiBold,
            });
            panel.Children.Add(new TextBlock
            {
                Text = T(item.Label), Foreground = dim, FontSize = small ? 9.5 : 10.5, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
            });
        }
        return panel;
    }

    private void BuildCenter()
    {
        foreach (var old in Ring.Children.OfType<FrameworkElement>().Where(c => c.Tag is "center").ToList()) Ring.Children.Remove(old);
        var stack = new StackPanel { Width = 2 * InnerR - 22, Tag = "center", IsHitTestVisible = false };
        _centerTitle = new TextBlock
        {
            Text = WheelKinds.Title(_kind).ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center,
        };
        _centerIcon = new WpfImage { Width = 32, Height = 32, Margin = new Thickness(0, 5, 0, 4) };
        _centerText = new TextBlock
        {
            FontSize = 11, Foreground = (Brush)FindResource("TextBrush"), TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        stack.Children.Add(_centerTitle);
        stack.Children.Add(_centerIcon);
        stack.Children.Add(_centerText);
        UpdateCenter();
        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(stack, Center.X - stack.Width / 2);
        Canvas.SetTop(stack, Center.Y - Math.Max(stack.DesiredSize.Height, 76) / 2);
        Ring.Children.Add(stack);
    }

    private void UpdateCenter()
    {
        if (_centerText is null) return;
        if (_files is null || _files.Count == 0)
        {
            _centerIcon.Source = null;
            _centerText.Text = T("Solte arquivos aqui");
            return;
        }
        _centerIcon.Source = ShellIcons.For(_files[0]);
        if (_hover >= 0 && _hover < _items.Count)
        {
            _centerText.Text = T(_items[_hover].Hint ?? _items[_hover].Label);
            _centerText.Foreground = (Brush)FindResource("AccentBrush");
        }
        else if (_items.Count == 0)
        {
            _centerText.Text = T("Nada disponível aqui. Tente outra tecla.");
            _centerText.Foreground = (Brush)FindResource("TextDimBrush");
        }
        else
        {
            _centerText.Text = _files.Count == 1 ? System.IO.Path.GetFileName(_files[0].TrimEnd('\\')) : Ui.Plural(_files.Count, "item", "itens");
            _centerText.Foreground = (Brush)FindResource("TextBrush");
        }
    }

    private void BuildChips()
    {
        Chips.Children.Clear();
        var keys = new (WheelKind Kind, string Key)[]
        {
            (WheelKind.Convert, _settings.KeyConvert),
            (WheelKind.Tools, _settings.KeyTools),
            (WheelKind.Actions, _settings.KeyActions),
            (WheelKind.Custom, _settings.KeyCustom),
        };
        foreach (var (kind, key) in keys)
        {
            if (key == "None" && kind != _kind) continue;
            var active = kind == _kind;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(1, 0, 1, 0),
                Background = active ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
                Cursor = _clickMode ? Cursors.Hand : null,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (key != "None")
            {
                row.Children.Add(new TextBlock
                {
                    Text = DragWatcher.KeyLabel(key), FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 5, 0),
                    Foreground = active ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("TextDimBrush"),
                });
            }
            row.Children.Add(new TextBlock
            {
                Text = WheelKinds.Title(kind), FontSize = 10.5,
                Foreground = active ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("TextBrush"),
            });
            chip.Child = row;
            if (_clickMode) chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; SwitchKind(kind); };
            Chips.Children.Add(chip);
        }
    }

    // ---------- hit testing ----------

    private int HitIndex(Point p)
    {
        if (_items.Count == 0) return -1;
        var v = p - Center;
        var d = v.Length;
        if (d < InnerR - 4 || d > OuterR + Pad + 40) return -1;
        var deg = Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        var fromTop = (deg + 90 + 360) % 360;
        var span = 360.0 / _items.Count;
        return (int)(((fromTop + span / 2) % 360) / span) % _items.Count;
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        if (_hover >= 0 && _hover < _slices.Count) _slices[_hover].Fill = SliceFill;
        _hover = index;
        if (_hover >= 0 && _hover < _slices.Count) _slices[_hover].Fill = _sliceHover;
        UpdateCenter();
    }

    // ---------- drag & drop ----------

    private static DragDropEffects Allowed(DragEventArgs e)
    {
        // Prefer Copy so the source never deletes anything; never answer Move.
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    private static IReadOnlyList<string>? FilesOf(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                return paths;
            }
        }
        catch
        {
            // Some sources throw while rendering formats lazily.
        }
        return null;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (_files is null)
        {
            var files = FilesOf(e.Data);
            App.Debug($"wheel DragEnter: {files?.Count ?? 0} file(s), allowed={e.AllowedEffects}, formats={string.Join(",", e.Data.GetFormats().Take(8))}");
            if (files is not null) SetFiles(files);
            else
            {
                _files = [];
                Rebuild();
            }
        }
        Reveal();
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var idx = HitIndex(e.GetPosition(Root));
        SetHover(idx);
        e.Effects = idx >= 0 ? Allowed(e) : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var idx = HitIndex(e.GetPosition(Root));
        e.Handled = true;
        App.Debug($"wheel Drop on slice {idx} ({(idx >= 0 && idx < _items.Count ? _items[idx].Label : "-")})");
        if (idx < 0 || _files is not { Count: > 0 } files)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = Allowed(e);
        Execute(_items[idx], files);
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var idx = HitIndex(e.GetPosition(Root));
        if (idx >= 0 && _files is { Count: > 0 } files) Execute(_items[idx], files);
        else if ((e.GetPosition(Root) - Center).Length > OuterR + Pad) Dismiss();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Dismiss();
            return;
        }
        var name = key switch
        {
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.Space => "Space",
            Key.Tab => "Tab",
            _ => null,
        };
        if (name is null) return;
        e.Handled = true;
        if (name == _settings.KeyConvert) SwitchKind(WheelKind.Convert);
        else if (name == _settings.KeyTools) SwitchKind(WheelKind.Tools);
        else if (name == _settings.KeyActions) SwitchKind(WheelKind.Actions);
        else if (name == _settings.KeyCustom) SwitchKind(WheelKind.Custom);
    }

    /// <summary>Called when the mouse button goes up after a drag: close unless something was dropped on us.</summary>
    public void OnDragEnded()
    {
        App.Debug("drag ended (button up)");
        if (_done) return;
        // The OLE drop arrives right after the button-up the hook saw; give it a moment.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_done) Dismiss();
        };
        t.Start();
    }

    private void Execute(WheelItem item, IReadOnlyList<string> files)
    {
        _done = true;
        Hide();
        // Run after the drop call returns, so the app we dragged from isn't kept waiting.
        Dispatcher.BeginInvoke(async () =>
        {
            Close();
            try
            {
                await item.Run(files);
            }
            catch (Exception ex)
            {
                Ui.Error($"{T(item.Label)}: {ex.Message}");
            }
        }, DispatcherPriority.Background);
    }

    public void Dismiss()
    {
        App.Debug("wheel dismissed");
        _done = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _done = true;
        base.OnClosed(e);
        Finished?.Invoke();
    }

    public bool IsClickMode => _clickMode;
    public bool IsDone => _done;
}
