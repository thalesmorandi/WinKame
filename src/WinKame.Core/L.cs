using System.Globalization;

namespace WinKame.Core;

/// <summary>
/// Interface language. Portuguese text is the key; <see cref="T(string)"/> returns the English
/// translation when the interface is in English. Picked from Windows' display language unless
/// the user chose one in Settings (or WINKAME_LANG is set).
/// </summary>
public static partial class L
{
    public static bool English { get; private set; }

    public static string Language => English ? "en" : "pt";

    static L() => Use(null);

    /// <param name="setting">"auto" (or null), "pt" or "en".</param>
    public static void Use(string? setting)
    {
        var env = Environment.GetEnvironmentVariable("WINKAME_LANG");
        var pick = string.IsNullOrWhiteSpace(env) ? setting : env;
        English = pick?.ToLowerInvariant() switch
        {
            "pt" => false,
            "en" => true,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "pt",
        };
    }

    public static string T(string pt) => English && En.TryGetValue(pt, out var en) ? en : pt;

    /// <summary>Translates a composite format string ("{0} de {1}") and fills it in.</summary>
    public static string T(string pt, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(pt), args);

    /// <summary>Every translated key, for the coverage test.</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable => En;
}
