// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinKame.Interop;

namespace WinKame;

internal static class Ui
{
    public static FontFamily IconFont { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Emoji, Segoe UI Symbol, Segoe UI");

    private static readonly FontFamily TextGlyphFont = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");

    /// <summary>True for Segoe icon code points (Private Use Area); letters and emoji are plain text.</summary>
    public static bool IsIconGlyph(string glyph) => glyph.Length > 0 && glyph[0] >= '' && glyph[0] <= '';

    /// <summary>The icon font maps ordinary characters like "#" to pictograms, so use it only for icons.</summary>
    public static FontFamily GlyphFont(string glyph) => IsIconGlyph(glyph) ? IconFont : TextGlyphFont;

    public static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public static Task<T> OnUiAsync<T>(Func<T> f)
    {
        var d = Application.Current.Dispatcher;
        return d.CheckAccess() ? Task.FromResult(f()) : d.InvokeAsync(f).Task;
    }

    public static BitmapSource? ToBitmap(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(data);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Applies the Windows 11 look to a standard window.</summary>
    public static void Themed(Window w)
    {
        w.ThemeMode = ThemeMode.System;
    }

    /// <summary>
    /// Centers a window on the monitor under the cursor. WPF's CenterScreen can land a window
    /// across two monitors when they use different scaling, so this works in physical pixels.
    /// </summary>
    public static void CenterOnCursorMonitor(Window w)
    {
        void Center()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
            var (work, _) = Native.MonitorAt(Native.CursorPos());
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            var x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
            var y = work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 2);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Loaded += (_, _) => Center();
    }

    /// <summary>Shows a modal window in front of everything, even though we're a background app.</summary>
    public static bool? ShowOnTop(Window w)
    {
        w.Topmost = true;
        CenterOnCursorMonitor(w);
        w.ShowInTaskbar = true;
        w.SourceInitialized += (_, _) => Native.ForceForeground(new WindowInteropHelper(w).Handle);
        w.ContentRendered += (_, _) =>
        {
            w.Activate();
            w.Topmost = false; // stay in front once, then behave normally
        };
        return w.ShowDialog();
    }

    public static void Info(string text, string title = "WinKame") =>
        Message(T(text), T(title), MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string text, string title = "WinKame") =>
        Message(T(text), T(title), MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string text, string title = "WinKame") =>
        Message(T(text), T(title), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Message(string text, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        // An invisible owner keeps the box on top of other apps.
        var owner = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = true, Topmost = true, Left = -10000, Top = -10000, AllowsTransparency = true, Opacity = 0,
        };
        owner.Show();
        Native.ForceForeground(new WindowInteropHelper(owner).Handle);
        try
        {
            return MessageBox.Show(owner, text, title, buttons, icon);
        }
        finally
        {
            owner.Close();
        }
    }

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {T(one)}" : $"{n} {T(many)}";
}
