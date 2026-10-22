using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinKame.Interop;

namespace WinKame.Shell;

internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Explorer icon for a file or folder (32px). Cached per extension.</summary>
    public static ImageSource? For(string path, bool small = false)
    {
        var isDir = Directory.Exists(path);
        var ext = Path.GetExtension(path);
        // Icons of .exe/.lnk/.ico differ per file, so don't share them.
        var perFile = ext is ".exe" or ".lnk" or ".ico" or ".url";
        var key = (small ? "s|" : "l|") + (isDir ? "<dir>" : perFile ? path : ext);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var info = new Native.SHFILEINFO();
        var flags = Native.SHGFI_ICON | (small ? Native.SHGFI_SMALLICON : Native.SHGFI_LARGEICON);
        uint attrs = 0;
        var query = path;
        if (!perFile && !isDir)
        {
            flags |= Native.SHGFI_USEFILEATTRIBUTES;
            attrs = 0x80; // FILE_ATTRIBUTE_NORMAL
            query = "x" + ext;
        }
        ImageSource? img = null;
        if (Native.SHGetFileInfo(query, attrs, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
            finally
            {
                Native.DestroyIcon(info.hIcon);
            }
        }
        lock (Cache) Cache[key] = img;
        return img;
    }
}
