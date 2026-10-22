// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinKame.Interop;

namespace WinKame.Shell;

/// <summary>Opens the Windows share sheet (Nearby sharing, Mail, Teams…) for files.</summary>
internal static class ShareHelper
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private static Window? _anchor;

    public static void Share(IReadOnlyList<string> paths)
    {
        // The share sheet needs a real window to attach to: a tiny one at the cursor.
        _anchor?.Close();
        var pt = Native.CursorPos();
        var (_, scale) = Native.MonitorAt(pt);
        _anchor = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Left = pt.X / scale, Top = pt.Y / scale, Topmost = true, Opacity = 0.01, AllowsTransparency = true,
        };
        _anchor.Show();
        var hwnd = new WindowInteropHelper(_anchor).Handle;
        Native.ForceForeground(hwnd);

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var ptr = interop.GetForWindow(hwnd, ref iid);
        var dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);
        dtm.DataRequested += async (_, e) =>
        {
            var deferral = e.Request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var p in paths)
                {
                    items.Add(System.IO.Directory.Exists(p)
                        ? await StorageFolder.GetFolderFromPathAsync(p)
                        : await StorageFile.GetFileFromPathAsync(p));
                }
                e.Request.Data.Properties.Title = paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : T("{0} arquivos", paths.Count);
                e.Request.Data.SetStorageItems(items);
            }
            catch (Exception ex)
            {
                e.Request.FailWithDisplayText(ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };
        interop.ShowShareUIForWindow(hwnd);

        // Close the anchor once the sheet had time to open; it doesn't need the window afterwards.
        var anchor = _anchor;
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => Ui.OnUi(() => anchor.Close()));
    }
}
