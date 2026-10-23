// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinKame.Core.Engines;
using WpfImage = System.Windows.Controls.Image;

namespace WinKame.Dialogs;

/// <summary>Draw a crop rectangle, or several areas to hide, on top of a picture.</summary>
public sealed class ImageRegionDialog : Window
{
    private readonly bool _redact;
    private readonly int _imgW, _imgH;
    private readonly double _scale; // image px per displayed px
    private readonly Canvas _canvas;
    private readonly Path _shade;
    private readonly Rectangle _sel;
    private readonly List<Rect> _redactions = [];
    private readonly ComboBox _option;
    private readonly TextBox _outW = new() { Width = 70 }, _outH = new() { Width = 70 };
    private readonly TextBlock _sizeLabel = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private Rect _current;
    private Point? _dragStart;
    private Vector _moveOffset;
    private bool _moving;
    // Edges being dragged when resizing the crop box from a side.
    private bool _edgeL, _edgeT, _edgeR, _edgeB;
    private Rect _before;
    private const double Grip = 9;

    private static readonly (string Label, double Ratio)[] Aspects =
    [
        (T("Livre"), 0), (T("Original"), -1), ("1:1", 1), ("4:3", 4 / 3.0), ("3:2", 1.5), ("16:9", 16 / 9.0),
        ("3:4", 0.75), ("4:5", 0.8), ("9:16", 9 / 16.0),
    ];

    private ImageRegionDialog(string path, bool redact, byte[] previewPng, int imgW, int imgH)
    {
        Ui.Themed(this);
        _redact = redact;
        _imgW = imgW;
        _imgH = imgH;
        Title = redact ? T("Ocultar áreas") : T("Recortar imagem");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        var bmp = Ui.ToBitmap(previewPng)!;
        var maxW = Math.Min(1000, SystemParameters.WorkArea.Width - 120);
        var maxH = Math.Min(680, SystemParameters.WorkArea.Height - 260);
        var fit = Math.Min(maxW / imgW, maxH / imgH);
        var dispW = Math.Max(80, imgW * fit);
        var dispH = Math.Max(80, imgH * fit);
        _scale = imgW / dispW;

        _canvas = new Canvas { Width = dispW, Height = dispH, Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
        _canvas.Children.Add(new WpfImage { Source = bmp, Width = dispW, Height = dispH, Stretch = Stretch.Fill });
        _shade = new Path { Fill = new SolidColorBrush(Color.FromArgb(redact ? (byte)0 : (byte)140, 0, 0, 0)), IsHitTestVisible = false };
        _canvas.Children.Add(_shade);
        _sel = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = [4, 3], IsHitTestVisible = false,
            Fill = redact ? new SolidColorBrush(Accent.WithAlpha(Accent.Current, 90)) : Brushes.Transparent,
        };
        _canvas.Children.Add(_sel);
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        if (redact)
        {
            toolbar.Children.Add(new TextBlock { Text = T("Cobrir com"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _option = new ComboBox { ItemsSource = new[] { T("Desfoque"), T("Pixelado"), T("Preto") }, SelectedIndex = 0, Width = 140 };
            toolbar.Children.Add(_option);
            var clear = new Button { Content = T("Limpar áreas"), Margin = new Thickness(12, 0, 0, 0) };
            clear.Click += (_, _) => { _redactions.Clear(); _current = Rect.Empty; Redraw(); };
            toolbar.Children.Add(clear);
            toolbar.Children.Add(new TextBlock { Text = T("Arraste sobre a imagem para marcar cada área."), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        }
        else
        {
            toolbar.Children.Add(new TextBlock { Text = T("Proporção"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _option = new ComboBox { ItemsSource = Aspects.Select(a => a.Label).ToArray(), SelectedIndex = 0, Width = 120 };
            _option.SelectionChanged += (_, _) => ApplyAspect();
            toolbar.Children.Add(_option);
            toolbar.Children.Add(new TextBlock { Text = T("Saída (px)"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
            toolbar.Children.Add(_outW);
            toolbar.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
            toolbar.Children.Add(_outH);
            toolbar.Children.Add(_sizeLabel);
            // Start inset so the box and the dimmed outside are visible and its edges easy to grab.
            _current = new Rect(dispW * 0.1, dispH * 0.1, dispW * 0.8, dispH * 0.8);
        }

        var ok = new Button { Content = redact ? T("Ocultar") : T("Recortar"), IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent) ok.Style = accent;
        ok.Click += (_, _) =>
        {
            if (_redact && _redactions.Count == 0 && _current.IsEmpty) return;
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(new Button { Content = T("Cancelar"), IsCancel = true, MinWidth = 90 });
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        root.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path) + $" · {imgW}×{imgH}", Opacity = 0.65, Margin = new Thickness(0, 0, 0, redact ? 12 : 2) });
        if (!redact)
        {
            root.Children.Add(new TextBlock { Text = T("Arraste para marcar a área. Arraste as bordas ou os cantos para ajustar e o meio para mover."), Opacity = 0.65, Margin = new Thickness(0, 0, 0, 12) });
        }
        root.Children.Add(toolbar);
        root.Children.Add(new Border { Child = _canvas, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => Redraw();
    }

    private double CurrentRatio()
    {
        if (_redact) return 0;
        var r = Aspects[Math.Max(0, _option.SelectedIndex)].Ratio;
        return r < 0 ? _imgW / (double)_imgH : r;
    }

    private void ApplyAspect()
    {
        var r = CurrentRatio();
        if (r <= 0) { Redraw(); return; }
        var rect = ImageEngine.CenterRect((int)_canvas.Width, (int)_canvas.Height, r);
        _current = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        Redraw();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
        _before = _current;
        _moving = false;
        _dragStart = null;
        _edgeL = _edgeT = _edgeR = _edgeB = false;
        var (l, t, r, b, inside) = HitTest(p);
        var corner = (l || r) && (t || b);
        if ((l || t || r || b) && (corner || CurrentRatio() > 0))
        {
            // Corner (or any edge with a fixed ratio): redraw from the opposite corner.
            if (!corner) { r = r || b; b = r; l = !r; t = !r; }
            _dragStart = new Point(l ? _current.Right : _current.Left, t ? _current.Bottom : _current.Top);
        }
        else if (l || t || r || b)
        {
            (_edgeL, _edgeT, _edgeR, _edgeB) = (l, t, r, b);
        }
        else if (inside)
        {
            _moving = true;
            _moveOffset = p - _current.TopLeft;
        }
        else
        {
            _dragStart = p;
            _current = new Rect(p, p);
        }
        Redraw();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _canvas.Cursor = CursorFor(p);
            return;
        }
        var bounds = new Rect(0, 0, _canvas.Width, _canvas.Height);
        if (_edgeL || _edgeT || _edgeR || _edgeB)
        {
            p = new Point(Math.Clamp(p.X, 0, bounds.Width), Math.Clamp(p.Y, 0, bounds.Height));
            var x1 = _edgeL ? p.X : _before.Left;
            var x2 = _edgeR ? p.X : _before.Right;
            var y1 = _edgeT ? p.Y : _before.Top;
            var y2 = _edgeB ? p.Y : _before.Bottom;
            _current = new Rect(new Point(Math.Min(x1, x2), Math.Min(y1, y2)), new Point(Math.Max(x1, x2), Math.Max(y1, y2)));
        }
        else if (_moving)
        {
            var x = Math.Clamp(p.X - _moveOffset.X, 0, bounds.Width - _current.Width);
            var y = Math.Clamp(p.Y - _moveOffset.Y, 0, bounds.Height - _current.Height);
            _current = new Rect(x, y, _current.Width, _current.Height);
        }
        else if (_dragStart is { } s)
        {
            p = new Point(Math.Clamp(p.X, 0, bounds.Width), Math.Clamp(p.Y, 0, bounds.Height));
            var w = Math.Abs(p.X - s.X);
            var h = Math.Abs(p.Y - s.Y);
            var r = CurrentRatio();
            if (r > 0)
            {
                if (w / Math.Max(1, h) > r) w = h * r;
                else h = w / r;
            }
            var x = p.X < s.X ? s.X - w : s.X;
            var y = p.Y < s.Y ? s.Y - h : s.Y;
            _current = Rect.Intersect(new Rect(x, y, w, h), bounds);
        }
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        _dragStart = null;
        _moving = false;
        _edgeL = _edgeT = _edgeR = _edgeB = false;
        // A plain click (no drag) keeps the previous crop box.
        if (!_redact && (_current.IsEmpty || _current.Width < 4 || _current.Height < 4)) _current = _before;
        if (_redact && !_current.IsEmpty && _current.Width > 3 && _current.Height > 3)
        {
            _redactions.Add(_current);
            _current = Rect.Empty;
        }
        Redraw();
    }

    /// <summary>Which edges of the crop box are under the pointer, and whether it is inside.</summary>
    private (bool L, bool T, bool R, bool B, bool Inside) HitTest(Point p)
    {
        if (_redact || _current.IsEmpty) return default;
        var c = _current;
        var nearX = p.X >= c.Left - Grip && p.X <= c.Right + Grip;
        var nearY = p.Y >= c.Top - Grip && p.Y <= c.Bottom + Grip;
        var l = nearY && Math.Abs(p.X - c.Left) <= Grip;
        var r = nearY && !l && Math.Abs(p.X - c.Right) <= Grip;
        var t = nearX && Math.Abs(p.Y - c.Top) <= Grip;
        var b = nearX && !t && Math.Abs(p.Y - c.Bottom) <= Grip;
        return (l, t, r, b, c.Contains(p));
    }

    private Cursor CursorFor(Point p)
    {
        var (l, t, r, b, inside) = HitTest(p);
        if ((l && t) || (r && b)) return Cursors.SizeNWSE;
        if ((r && t) || (l && b)) return Cursors.SizeNESW;
        if (l || r) return Cursors.SizeWE;
        if (t || b) return Cursors.SizeNS;
        return inside ? Cursors.SizeAll : Cursors.Cross;
    }

    private void Redraw()
    {
        var full = new RectangleGeometry(new Rect(0, 0, _canvas.Width, _canvas.Height));
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(full);
        if (!_redact && !_current.IsEmpty) group.Children.Add(new RectangleGeometry(_current));
        _shade.Data = group;

        // Previous redaction boxes stay visible as filled shapes.
        foreach (var old in _canvas.Children.OfType<Rectangle>().Where(r => r.Tag is "area").ToList()) _canvas.Children.Remove(old);
        foreach (var r in _redactions)
        {
            var box = new Rectangle
            {
                Width = r.Width, Height = r.Height, Tag = "area", IsHitTestVisible = false,
                Fill = new SolidColorBrush(Accent.WithAlpha(Accent.Current, 110)), Stroke = Brushes.White, StrokeThickness = 1,
            };
            Canvas.SetLeft(box, r.X);
            Canvas.SetTop(box, r.Y);
            _canvas.Children.Add(box);
        }

        if (_current.IsEmpty)
        {
            _sel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _sel.Visibility = Visibility.Visible;
            _sel.Width = _current.Width;
            _sel.Height = _current.Height;
            Canvas.SetLeft(_sel, _current.X);
            Canvas.SetTop(_sel, _current.Y);
        }
        if (!_redact && !_current.IsEmpty)
        {
            var px = ToPixels(_current);
            _sizeLabel.Text = $"{px.Width}×{px.Height}";
        }
    }

    private PixelRect ToPixels(Rect r) =>
        new((int)Math.Round(r.X * _scale), (int)Math.Round(r.Y * _scale),
            Math.Max(1, (int)Math.Round(r.Width * _scale)), Math.Max(1, (int)Math.Round(r.Height * _scale)));

    private static int ParseOr0(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) && v > 0 ? v : 0;

    private static (byte[] Png, int W, int H) LoadPreview(string path)
    {
        using var img = ImageEngine.Load(path);
        var w = (int)img.Width;
        var h = (int)img.Height;
        if (img.Width > 1400 || img.Height > 1400) img.Thumbnail(1400, 1400);
        return (ImageEngine.ToPng(img), w, h);
    }

    /// <summary>Builds the crop window without showing it (used by --snapshot).</summary>
    internal static Window BuildCrop(string path)
    {
        var (png, w, h) = LoadPreview(path);
        return new ImageRegionDialog(path, false, png, w, h);
    }

    public static (PixelRect Rect, int OutWidth, int OutHeight)? Crop(string path)
    {
        var (png, w, h) = LoadPreview(path);
        var dlg = new ImageRegionDialog(path, false, png, w, h);
        if (Ui.ShowOnTop(dlg) != true || dlg._current.IsEmpty || dlg._current.Width < 2) return null;
        return (dlg.ToPixels(dlg._current), ParseOr0(dlg._outW.Text), ParseOr0(dlg._outH.Text));
    }

    public static (IReadOnlyList<PixelRect> Areas, RedactMode Mode)? Redact(string path)
    {
        var (png, w, h) = LoadPreview(path);
        var dlg = new ImageRegionDialog(path, true, png, w, h);
        if (Ui.ShowOnTop(dlg) != true) return null;
        var areas = dlg._redactions.Select(dlg.ToPixels).ToList();
        if (areas.Count == 0) return null;
        var mode = dlg._option.SelectedIndex switch { 1 => RedactMode.Pixelate, 2 => RedactMode.Fill, _ => RedactMode.Blur };
        return (areas, mode);
    }
}
