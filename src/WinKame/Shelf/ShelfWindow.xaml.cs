// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinKame.Interop;
using WinKame.Shell;

namespace WinKame.Shelf;

public sealed record ShelfItem(string Path, string Name, string Folder, ImageSource? Icon);

/// <summary>A floating, always-on-top holding area for files.</summary>
public partial class ShelfWindow : Window
{
    private readonly ObservableCollection<ShelfItem> _items = [];
    private Point? _dragStart;

    public ShelfWindow()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateState();
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        Drop += OnDrop;
        UpdateState();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (_items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(p) && !Directory.Exists(p)) continue;
            var trimmed = p.TrimEnd('\\');
            _items.Add(new ShelfItem(p, Path.GetFileName(trimmed), Path.GetDirectoryName(trimmed) ?? "", ShellIcons.For(p)));
        }
        if (!IsVisible)
        {
            if (Left == 0 && Top == 0 || double.IsNaN(Left)) DockTo(right: true);
            Show();
        }
    }

    private void UpdateState()
    {
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DragAll.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DragAllText.Text = T("Arrastar todos ({0})", _items.Count);
    }

    private void DockTo(bool right)
    {
        var (work, scale) = Native.MonitorAt(Native.CursorPos());
        var w = work.Right / scale - work.Left / scale;
        var h = work.Bottom / scale - work.Top / scale;
        var current = double.IsNaN(Height) || Height < 160 ? 380 : Height;
        Height = Math.Min(current, h - 40);
        Top = work.Top / scale + (h - Height) / 2;
        Left = right ? work.Left / scale + w - Width : work.Left / scale;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // Ignore drags that started from the shelf itself.
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _dragStart is null ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_dragStart is null && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void StartDrag(IReadOnlyList<ShelfItem> items)
    {
        if (items.Count == 0) return;
        var list = new StringCollection();
        list.AddRange(items.Select(i => i.Path).ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        _dragStart = null;
        // If the target moved the files, they are no longer here.
        if (result == DragDropEffects.Move)
        {
            foreach (var i in items.Where(i => !File.Exists(i.Path) && !Directory.Exists(i.Path)).ToList()) _items.Remove(i);
        }
    }

    private void Items_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void Items_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } s) return;
        var d = e.GetPosition(this) - s;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var selected = Items.SelectedItems.Cast<ShelfItem>().ToList();
        if (selected.Count == 0 && (e.OriginalSource as FrameworkElement)?.DataContext is ShelfItem under) selected = [under];
        StartDrag(selected);
    }

    private void DragAll_Down(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        e.Handled = true;
    }

    private void DragAll_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _dragStart is not null) StartDrag(_items.ToList());
    }

    private void Items_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            foreach (var i in Items.SelectedItems.Cast<ShelfItem>().ToList()) _items.Remove(i);
        }
        else if (e.Key == Key.Enter && Items.SelectedItem is ShelfItem item)
        {
            FileOps.Reveal([item.Path]);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void DockLeft_Click(object sender, RoutedEventArgs e) => DockTo(right: false);
    private void DockRight_Click(object sender, RoutedEventArgs e) => DockTo(right: true);
    private void Clear_Click(object sender, RoutedEventArgs e) => _items.Clear();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        Hide();
    }
}
