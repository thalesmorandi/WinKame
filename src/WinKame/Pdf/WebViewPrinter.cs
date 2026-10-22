// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using WinKame.Core.Engines;

namespace WinKame.Pdf;

/// <summary>
/// Prints local HTML to PDF with WebView2 (the Edge engine that ships with Windows 10/11).
/// Runs hidden, with scripts off and every network request blocked, so nothing leaves the PC.
/// </summary>
internal sealed class WebViewPrinter
{
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _controller;
    private HwndSource? _host;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Registers the printer with the core so document → PDF conversions can use it.</summary>
    public static void Install()
    {
        var printer = new WebViewPrinter();
        NativeDocs.HtmlFileToPdf = (html, pdf, ct) => Ui.OnUiAsync(() => printer.PrintAsync(html, pdf, ct)).Unwrap();
    }

    public static bool RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    private async Task EnsureAsync()
    {
        if (_controller is not null) return;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKame", "webview2");
        _env = await CoreWebView2Environment.CreateAsync(null, data);

        // A hidden window to host the browser; it is never shown.
        _host = new HwndSource(new HwndSourceParameters("WinKame.Print")
        {
            Width = 900,
            Height = 1200,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, not visible
        });
        _controller = await _env.CreateCoreWebView2ControllerAsync(_host.Handle);
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, 900, 1200);
        _controller.IsVisible = false;

        var wv = _controller.CoreWebView2;
        wv.Settings.IsScriptEnabled = false;
        wv.Settings.AreDefaultContextMenusEnabled = false;
        wv.Settings.AreDevToolsEnabled = false;
        wv.Settings.IsStatusBarEnabled = false;
        wv.Settings.IsWebMessageEnabled = false;

        // Offline guarantee: only the local file and inline data may load.
        wv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        wv.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
            }
        };
    }

    private async Task PrintAsync(string htmlPath, string pdfPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureAsync();
            var wv = _controller!.CoreWebView2;
            var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => nav.TrySetResult(e.IsSuccess);
            wv.NavigationCompleted += Done;
            try
            {
                wv.Navigate(new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri);
                var ok = await nav.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
                if (!ok) throw new InvalidOperationException(T("O WebView2 não conseguiu abrir o documento."));
            }
            finally
            {
                wv.NavigationCompleted -= Done;
            }

            var settings = _env!.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.PageWidth = 8.27;   // A4, inches
            settings.PageHeight = 11.69;
            settings.MarginTop = settings.MarginBottom = 0.7;
            settings.MarginLeft = settings.MarginRight = 0.65;
            if (!await wv.PrintToPdfAsync(Path.GetFullPath(pdfPath), settings))
            {
                throw new InvalidOperationException(T("O WebView2 não conseguiu gerar o PDF."));
            }
            wv.Navigate("about:blank");
        }
        finally
        {
            _gate.Release();
        }
    }
}
