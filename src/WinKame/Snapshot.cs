using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinKame.Core;
using WinKame.Dialogs;
using WinKame.Jobs;
using WinKame.Settings;
using WinKame.Shelf;
using WinKame.Wheel;

namespace WinKame;

/// <summary>
/// Developer aid: WinKame.exe --snapshot &lt;target&gt; &lt;out.png&gt; [files…]
/// renders a window off-screen to a PNG, so the UI can be checked without screenshots of the desktop.
/// Targets: settings[:tab], wheel:&lt;kind&gt;, form, jobs, shelf.
/// </summary>
internal static class Snapshot
{
    public static async Task RunAsync(string[] args, AppSettings settings)
    {
        var target = args.ElementAtOrDefault(1) ?? "settings";
        var outPath = Path.GetFullPath((args.ElementAtOrDefault(2) ?? "snapshot.png").Trim());
        var files = args.Skip(3).Select(a => a.Trim()).ToList();
        var parts = target.Split(':');

        Window w;
        switch (parts[0])
        {
            case "wheel":
                var kind = Enum.TryParse<WheelKind>(parts.ElementAtOrDefault(1), true, out var k) ? k : WheelKind.Convert;
                var wheel = new WheelWindow(kind, new Interop.Native.POINT(0, 0), files, clickMode: true, settings) { SnapshotMode = true };
                wheel.RevealInstantly();
                w = wheel;
                break;
            case "form":
                w = FormDialog.Build("Comprimir", Ui.Plural(2, "imagem", "imagens"),
                [
                    new SliderField("q", "Qualidade", 10, 100, 75, 1, "%"),
                    new NumberField("kb", "Tamanho alvo, 0 = usar a qualidade", 0, 0, 500_000, "KB"),
                    new ChoiceField("level", "Compressão", ["Leve", "Média", "Forte"], 1),
                    new CheckField("jpg", "Salvar como JPG"),
                    new NoteField("PNG é reduzido para 256 cores. Os originais não são alterados."),
                ], "Comprimir");
                break;
            case "jobs":
                JobRunner.All.Add(new Job { Title = T("Comprimindo imagens"), Detail = T("{0} de {1}", 2, 3) + " · foto.png", Progress = 0.45 });
                var done = new Job { Title = T("Convertendo para {0}", "PDF"), Detail = "relatorio.pdf", Progress = 1 };
                done.Outputs.Add(files.FirstOrDefault() ?? outPath);
                done.State = JobState.Done;
                JobRunner.All.Add(done);
                w = new JobsWindow();
                break;
            case "shelf":
                var shelf = new ShelfWindow();
                shelf.AddFiles(files);
                shelf.Hide();
                w = shelf;
                break;
            default:
                w = new SettingsWindow(settings, parts.ElementAtOrDefault(1));
                break;
        }

        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000;
        w.Top = -32000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);

        var content = (FrameworkElement)w.Content;
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Themed windows use the system backdrop, which a bitmap can't capture; paint the theme color.
            var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 1;
            var bg = w.AllowsTransparency ? Brushes.DimGray : new SolidColorBrush(light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20));
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) encoder.Save(fs);
        w.Close();
        Console.Out.WriteLine(outPath);
    }
}
