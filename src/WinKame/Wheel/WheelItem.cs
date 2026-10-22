namespace WinKame.Wheel;

public enum WheelKind
{
    Convert,
    Tools,
    Actions,
    Custom,
}

/// <summary>One slice of the wheel.</summary>
/// <param name="Label">Short text under the icon (or the format name for conversions).</param>
/// <param name="Glyph">An icon character; ignored when <paramref name="IsFormat"/> is true.</param>
/// <param name="Run">What happens when files are dropped on the slice.</param>
/// <param name="Hint">Longer description shown in the center while hovering.</param>
public sealed record WheelItem(string Label, string Glyph, Func<IReadOnlyList<string>, Task> Run, string? Hint = null, bool IsFormat = false);

public static class WheelKinds
{
    public static string Title(WheelKind k) => k switch
    {
        WheelKind.Convert => T("Converter"),
        WheelKind.Tools => T("Ferramentas"),
        WheelKind.Actions => T("Ações"),
        _ => T("Personalizadas"),
    };
}
