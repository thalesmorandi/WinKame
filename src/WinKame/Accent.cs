// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinKame;

/// <summary>
/// The highlight color of WinKame's own surfaces: the wheel, the jobs card, the shelf and progress bars.
/// Standard buttons keep the Windows accent. The setting is "#RRGGBB" or "system" (follow Windows).
/// </summary>
internal static class Accent
{
    public const string Default = "#FF8A1F";
    public const string FollowWindows = "system";

    /// <summary>Choices offered in the settings, the WinKame orange first.</summary>
    public static readonly string[] Presets = [Default, "#E5487A", "#7C5CFF", "#2D9CDB", "#14B8A6", "#27AE60", "#F2C94C", "#EF4444"];

    private static string _setting = Default;

    public static Color Current { get; private set; } = Parse(Default);

    public static string Hex => $"#{Current.R:X2}{Current.G:X2}{Current.B:X2}";

    /// <summary>Applies the color to the shared resources; windows using DynamicResource update at once.</summary>
    public static void Apply(string? setting)
    {
        _setting = string.IsNullOrWhiteSpace(setting) ? Default : setting.Trim();
        var c = Resolve(_setting);
        Current = c;
        var r = Application.Current.Resources;
        r["AccentColor"] = c;
        r["AccentBrush"] = Frozen(new SolidColorBrush(c));
        r["AccentSoftBrush"] = Frozen(new SolidColorBrush(WithAlpha(c, 0x33)));
        r["OnAccentBrush"] = Frozen(new SolidColorBrush(OnAccent(c)));
    }

    /// <summary>Re-applies when Windows' accent changes, if the setting follows it.</summary>
    public static void WatchWindows()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && _setting == FollowWindows)
            {
                Application.Current.Dispatcher.BeginInvoke(() => Apply(_setting));
            }
        };
    }

    public static Color Resolve(string? setting)
    {
        if (setting == FollowWindows) return WindowsAccent() ?? Parse(Default);
        return TryParse(setting, out var c) ? c : Parse(Default);
    }

    /// <summary>The wheel's hovered slice: a soft vertical gradient around the accent.</summary>
    public static Brush SliceHoverBrush() =>
        Frozen(new LinearGradientBrush(Mix(Current, Colors.White, 0.12), Mix(Current, Colors.Black, 0.10), 90));

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>White text on dark or saturated accents, near-black on light ones (yellow, light teal).</summary>
    public static Color OnAccent(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        return luminance > 0.5 ? Color.FromRgb(0x1E, 0x1E, 0x21) : Colors.White;
    }

    public static Color? WindowsAccent()
    {
        try
        {
            var v = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromRgb(v.R, v.G, v.B);
        }
        catch
        {
            return null;
        }
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    private static Color Parse(string hex) => TryParse(hex, out var c) ? c : Colors.Orange;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
