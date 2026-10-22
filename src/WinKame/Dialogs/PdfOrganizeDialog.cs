// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinKame.Core.Engines;
using WpfImage = System.Windows.Controls.Image;

namespace WinKame.Dialogs;

/// <summary>Reorder, rotate, duplicate or remove PDF pages using thumbnails.</summary>
public sealed class PdfOrganizeDialog : Window
{
    private sealed class Page
    {
        public int Source;
        public int Rotation;
        public BitmapSource? Thumb;
    }

    private readonly List<Page> _pages = [];
    private readonly WrapPanel _wrap = new();
    private readonly TextBlock _count = new() { Opacity = 0.65 };

    private PdfOrganizeDialog(string file, IReadOnlyList<BitmapSource?> thumbs)
    {
        Ui.Themed(this);
        Title = T("Organizar páginas");
        Width = Math.Min(1040, SystemParameters.WorkArea.Width - 80);
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 60);

        for (var i = 0; i < thumbs.Count; i++) _pages.Add(new Page { Source = i, Thumb = thumbs[i] });

        var ok = new Button { Content = T("Salvar PDF"), IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent) ok.Style = accent;
        ok.Click += (_, _) => { if (_pages.Count > 0) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(new Button { Content = T("Cancelar"), IsCancel = true, MinWidth = 90 });
        buttons.Children.Add(ok);

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = T("Organizar páginas"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(file), Opacity = 0.65 });
        header.Children.Add(_count);

        var root = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = _wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Rebuild();
    }

    private void Rebuild()
    {
        _wrap.Children.Clear();
        _count.Text = T("{0} página(s) no resultado", _pages.Count);
        for (var i = 0; i < _pages.Count; i++) _wrap.Children.Add(Card(i));
    }

    private FrameworkElement Card(int index)
    {
        var page = _pages[index];
        var img = new WpfImage
        {
            Source = page.Thumb, Width = 132, Height = 170, Stretch = Stretch.Uniform,
            LayoutTransform = new RotateTransform(page.Rotation),
        };
        var imgHost = new Border { Width = 176, Height = 176, Child = img, Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)), CornerRadius = new CornerRadius(6) };

        Button B(string glyph, string tip, Action a)
        {
            var b = new Button { Content = glyph, FontFamily = Ui.IconFont, FontSize = 12, ToolTip = tip, Padding = new Thickness(6), Margin = new Thickness(1) };
            b.Click += (_, _) => { a(); Rebuild(); };
            return b;
        }
        var tools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        tools.Children.Add(B("", T("Mover para a esquerda"), () => Swap(index, index - 1)));
        tools.Children.Add(B("", T("Girar 90°"), () => page.Rotation = (page.Rotation + 90) % 360));
        tools.Children.Add(B("", T("Duplicar"), () => _pages.Insert(index + 1, new Page { Source = page.Source, Rotation = page.Rotation, Thumb = page.Thumb })));
        tools.Children.Add(B("", T("Remover"), () => _pages.RemoveAt(index)));
        tools.Children.Add(B("", T("Mover para a direita"), () => Swap(index, index + 1)));

        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(imgHost);
        stack.Children.Add(new TextBlock { Text = T("{0}  ·  pág. {1}", index + 1, page.Source + 1), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.75 });
        stack.Children.Add(tools);
        return stack;
    }

    private void Swap(int a, int b)
    {
        if (b < 0 || b >= _pages.Count) return;
        (_pages[a], _pages[b]) = (_pages[b], _pages[a]);
    }

    public static async Task<IReadOnlyList<PageOp>?> Show(string file)
    {
        var doc = await PdfEngine.OpenForRenderAsync(file);
        var thumbs = new List<BitmapSource?>();
        var max = Math.Min((int)doc.PageCount, 400);
        for (var i = 0; i < max; i++) thumbs.Add(Ui.ToBitmap(await PdfEngine.RenderPageAsync(doc, i, 200)));
        var dlg = new PdfOrganizeDialog(file, thumbs);
        if (Ui.ShowOnTop(dlg) != true) return null;
        return dlg._pages.Select(p => new PageOp(p.Source, p.Rotation)).ToList();
    }
}
