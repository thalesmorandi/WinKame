using System.IO;
using Microsoft.Win32;

namespace WinKame.Shell;

/// <summary>Per-user (no admin) hooks into Explorer: context menu, Send To and start with Windows.</summary>
internal static class ShellIntegration
{
    private const string VerbName = "WinKame";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinKame.exe");

    private static readonly string[] MenuRoots =
    [
        @"Software\Classes\*\shell\" + VerbName,
        @"Software\Classes\Directory\shell\" + VerbName,
    ];

    public static bool ContextMenuInstalled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(MenuRoots[0] + @"\command");
            return k?.GetValue(null) is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetContextMenu(bool on)
    {
        foreach (var root in MenuRoots)
        {
            if (on)
            {
                using var k = Registry.CurrentUser.CreateSubKey(root);
                k.SetValue("MUIVerb", T("Abrir no WinKame"));
                k.SetValue("Icon", $"\"{Exe}\",0");
                k.SetValue("MultiSelectModel", "Player");
                using var cmd = k.CreateSubKey("command");
                cmd.SetValue(null, $"\"{Exe}\" --wheel \"%1\"");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            }
        }
    }

    private static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "WinKame.lnk");

    public static bool SendToInstalled => File.Exists(SendToLink);

    /// <summary>"Send To" passes every selected file in one command line, which works well for selections.</summary>
    public static void SetSendTo(bool on)
    {
        if (!on)
        {
            if (File.Exists(SendToLink)) File.Delete(SendToLink);
            return;
        }
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponível");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(SendToLink);
            link.TargetPath = Exe;
            link.Arguments = "--wheel";
            link.IconLocation = Exe + ",0";
            link.Description = T("Abrir arquivos na roda do WinKame");
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static bool StartupInstalled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(VerbName) is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(VerbName, $"\"{Exe}\" --background");
        else k.DeleteValue(VerbName, throwOnMissingValue: false);
    }
}
