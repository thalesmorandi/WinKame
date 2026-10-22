using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using WinKame.Interop;

namespace WinKame.Shell;

internal static class FileOps
{
    /// <summary>Opens Explorer with the given items selected (grouped by folder).</summary>
    public static void Reveal(IEnumerable<string> paths)
    {
        foreach (var group in paths.Where(p => File.Exists(p) || Directory.Exists(p))
                     .GroupBy(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p))
        {
            var folder = Native.ILCreateFromPathW(group.Key);
            var items = group.Select(Native.ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
            try
            {
                if (folder != IntPtr.Zero && Native.SHOpenFolderAndSelectItems(folder, (uint)items.Length, items, 0) == 0) continue;
            }
            finally
            {
                if (folder != IntPtr.Zero) Native.ILFree(folder);
                foreach (var i in items) Native.ILFree(i);
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{group.First()}\"") { UseShellExecute = true });
        }
    }

    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Puts the files on the clipboard so Ctrl+V in Explorer pastes them.</summary>
    public static void CopyToClipboard(IReadOnlyList<string> paths)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        Retry(() => Clipboard.SetDataObject(data, true));
    }

    public static void CopyPaths(IReadOnlyList<string> paths) =>
        Retry(() => Clipboard.SetText(string.Join(Environment.NewLine, paths)));

    private static void Retry(Action a)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                a();
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (i < 5)
            {
                Thread.Sleep(60); // another app has the clipboard open
            }
        }
    }

    private static string? PickFolder(string title, string? start)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = start ?? "" };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>Moves or copies with the standard Windows progress and conflict dialogs.</summary>
    public static void Transfer(IReadOnlyList<string> paths, bool move)
    {
        var start = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
        var dest = PickFolder(move ? T("Mover para…") : T("Copiar para…"), start);
        if (dest is null) return;
        var moved = new List<string>();
        foreach (var p in paths)
        {
            var target = Path.Combine(dest, Path.GetFileName(p.TrimEnd('\\')));
            try
            {
                if (Directory.Exists(p))
                {
                    if (move) FileSystem.MoveDirectory(p, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    else FileSystem.CopyDirectory(p, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                else
                {
                    if (move) FileSystem.MoveFile(p, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                    else FileSystem.CopyFile(p, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                moved.Add(target);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        if (moved.Count > 0) Reveal(moved);
    }

    public static void OpenTerminal(string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt))
        {
            Process.Start(new ProcessStartInfo(wt, $"-d \"{dir.TrimEnd('\\')}\\.\"") { UseShellExecute = true });
            return;
        }
        Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = dir });
    }

    public static void OpenWith(string file)
    {
        var info = new Native.OPENASINFO
        {
            pcszFile = file,
            oaifInFlags = Native.OAIF_EXEC | Native.OAIF_ALLOW_REGISTRATION,
        };
        Native.SHOpenWithDialog(IntPtr.Zero, ref info);
    }
}
