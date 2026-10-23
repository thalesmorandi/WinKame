// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinKame.Core;

public sealed class AppSettings
{
    public bool DragWheelEnabled { get; set; } = true;

    /// <summary>Interface language: "auto" (Windows' language), "pt" or "en".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Highlight color of the wheel, jobs card and shelf: "#RRGGBB", or "system" to follow Windows.</summary>
    public string AccentColor { get; set; } = "#FF8A1F";

    /// <summary>Key tapped during a drag for each wheel: Shift, Ctrl, Alt, Space, Tab or None.</summary>
    public string KeyConvert { get; set; } = "Shift";
    public string KeyTools { get; set; } = "Ctrl";
    public string KeyActions { get; set; } = "Alt";
    public string KeyCustom { get; set; } = "Space";

    /// <summary>A press longer than this is a hold, not a tap.</summary>
    public int TapMaxMs { get; set; } = 450;

    /// <summary>How far the mouse must move with the button down before it counts as a drag.</summary>
    public int DragThresholdPx { get; set; } = 10;

    public double WheelScale { get; set; } = 1.0;

    /// <summary>Null or empty: save results next to the original.</summary>
    public string? OutputFolder { get; set; }

    public bool RevealWhenDone { get; set; }

    public bool StartWithWindows { get; set; }
    public bool ExplorerContextMenu { get; set; }
    public bool SendToMenu { get; set; }
    public bool ShowWelcome { get; set; } = true;

    public Dictionary<string, string> ToolPaths { get; set; } = new();

    /// <summary>Use Microsoft Word/Excel/PowerPoint when installed (best fidelity for Office files).</summary>
    public bool UseOffice { get; set; } = true;

    public List<CustomAction> CustomActions { get; set; } = [];

    // ---------- persistence ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("WINKAME_HOME");
            var dir = string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinKame")
                : custom;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s is not null) return s;
            }
        }
        catch
        {
            // A broken file falls back to defaults; keep a copy for the user.
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
        }
        var fresh = new AppSettings();
        fresh.CustomActions.AddRange(CustomAction.Examples().Take(2));
        return fresh;
    }

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>Pushes the settings that the engines read into their static state.</summary>
    public void ApplyToEngines()
    {
        OutputPaths.OverrideDir = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder;
        Engines.OfficeEngine.Disabled = !UseOffice || Environment.GetEnvironmentVariable("WINKAME_NO_OFFICE") == "1";
        ToolLocator.Overrides.Clear();
        foreach (var (k, v) in ToolPaths)
        {
            if (Enum.TryParse<ExternalTool>(k, out var t) && !string.IsNullOrWhiteSpace(v)) ToolLocator.Overrides[t] = v;
        }
        ToolLocator.Invalidate();
    }
}
