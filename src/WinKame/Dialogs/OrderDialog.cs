using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinKame.Shell;

namespace WinKame.Dialogs;

/// <summary>Lets the user put files in order before joining or merging them.</summary>
public sealed class OrderDialog : Window
{
    private readonly ListBox _list;

    private OrderDialog(string title, string subtitle, IReadOnlyList<string> files, string ok)
    {
        Ui.Themed(this);
        title = T(title);
        subtitle = T(subtitle);
        ok = T(ok);
        Title = title;
        Width = 520;
        Height = 520;
        MinHeight = 320;

        _list = new ListBox { SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var f in Sorted(files)) _list.Items.Add(Item(f));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        Button IconButton(string glyph, string tip, Action a)
        {
            var b = new Button { Content = glyph, FontFamily = Ui.IconFont, ToolTip = tip, Margin = new Thickness(0, 0, 0, 6), Width = 40, Height = 34 };
            b.Click += (_, _) => a();
            return b;
        }

        var side = new StackPanel { Margin = new Thickness(10, 12, 0, 0) };
        side.Children.Add(IconButton("", T("Subir"), () => Move(-1)));
        side.Children.Add(IconButton("", T("Descer"), () => Move(1)));
        side.Children.Add(IconButton("", T("Ordenar por nome"), SortByName));

        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent) okButton.Style = accent;
        okButton.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(new Button { Content = T("Cancelar"), IsCancel = true, MinWidth = 90 });
        buttons.Children.Add(okButton);

        var root = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(side);
        root.Children.Add(_list);
        Content = root;
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> files) =>
        files.OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance);

    private static ListBoxItem Item(string path)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Image { Source = ShellIcons.For(path, small: true), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = path, Padding = new Thickness(8, 6, 8, 6) };
    }

    private void Move(int delta)
    {
        var i = _list.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _list.Items.Count) return;
        var item = _list.Items[i];
        _list.Items.RemoveAt(i);
        _list.Items.Insert(j, item);
        _list.SelectedIndex = j;
    }

    private void SortByName()
    {
        var paths = _list.Items.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList();
        _list.Items.Clear();
        foreach (var p in Sorted(paths)) _list.Items.Add(Item(p));
    }

    public static IReadOnlyList<string>? Show(string title, string subtitle, IReadOnlyList<string> files, string ok)
    {
        var dlg = new OrderDialog(title, subtitle, files, ok);
        return Ui.ShowOnTop(dlg) == true ? dlg._list.Items.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList() : null;
    }
}

/// <summary>Sorts "2.jpg" before "10.jpg", like Explorer.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
