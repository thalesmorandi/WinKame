// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO.Compression;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using WinKame.Core;
using WinKame.Core.Engines;
using PdfMetadata = WinKame.Core.Engines.PdfMetadata;

namespace WinKame.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wq-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public static class Fixtures
{
    static Fixtures()
    {
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        ImageEngine.Init();
        // Tests exercise the native engines and must never start Word/Excel/PowerPoint.
        OfficeEngine.Disabled = true;
    }

    public static string Png(TempDir t, string name = "foto.png", int w = 640, int h = 480, bool alpha = false)
    {
        var path = t.File(name);
        using var img = new MagickImage("plasma:orange-purple", new MagickReadSettings { Width = (uint)w, Height = (uint)h });
        if (alpha)
        {
            img.Alpha(AlphaOption.Set);
            img.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.5);
        }
        img.Write(path, MagickFormat.Png);
        return path;
    }

    public static string TextPdf(TempDir t, string name = "doc.pdf", int pages = 3)
    {
        var path = t.File(name);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"Titulo da pagina {i + 1}", new XFont("Arial", 28, XFontStyleEx.Bold), XBrushes.Black, new XPoint(60, 90));
            g.DrawString("Um paragrafo de texto comum para teste.", new XFont("Arial", 11), XBrushes.Black, new XPoint(60, 140));
            g.DrawString("Nome      Valor", new XFont("Arial", 11), XBrushes.Black, new XPoint(60, 180));
        }
        doc.Info.Title = "Original";
        doc.Save(path);
        return path;
    }
}

public class FormatsTests
{
    [Fact]
    public void DetectsKindsAndTargets()
    {
        Assert.Equal(FileKind.Image, Formats.KindOf("a.JPEG"));
        Assert.Equal(FileKind.Archive, Formats.KindOf("b.tar.gz"));
        Assert.Equal("tar.gz", Formats.Ext(@"C:\x\b.tar.gz"));
        Assert.DoesNotContain("jpg", Formats.TargetsFor("x.jpeg"));
        Assert.Contains("png", Formats.TargetsFor("x.jpeg"));
        Assert.Contains("mp4", Formats.TargetsFor("anim.gif"));
    }

    [Fact]
    public void CommonTargetsIntersect()
    {
        var t = Formats.CommonTargets(["a.png", "b.jpg"]);
        Assert.Contains("webp", t);
        Assert.Contains("jpg", t); // the png still needs it
        Assert.Contains("png", t);
        var mixed = Formats.CommonTargets(["a.png", "b.mp3"]);
        Assert.Empty(mixed);
    }

    [Fact]
    public void UniqueNamesNeverOverwrite()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("a.txt"), "x");
        Assert.Equal(t.File("a (2).txt"), OutputPaths.Unique(t.Path, "a", "txt"));
        Assert.Equal("arquivo", OutputPaths.StemOf(@"C:\x\arquivo.tar.gz"));
    }
}

public class ImageTests
{
    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("ico")]
    [InlineData("avif")]
    [InlineData("pdf")]
    public void ConvertsPng(string target)
    {
        using var t = new TempDir();
        var src = Fixtures.Png(t, alpha: true);
        var outPath = ImageEngine.Convert(src, target);
        Assert.True(File.Exists(outPath), outPath);
        Assert.EndsWith("." + target, outPath);
        Assert.True(new FileInfo(outPath).Length > 100);
    }

    [Fact]
    public void ReportsHeicSupport()
    {
        // Either works or is filtered out of the wheel; it must not throw.
        var can = ImageEngine.CanWrite("heic");
        Assert.Equal(can, Formats.TargetsFor("a.png").Contains("heic"));
    }

    [Fact]
    public void CompressHitsTargetSize()
    {
        using var t = new TempDir();
        var src = Fixtures.Png(t, "big.png", 2000, 1500);
        var jpg = ImageEngine.Convert(src, "jpg");
        var outPath = ImageEngine.Compress(jpg, new ImageCompressOptions(TargetKb: 60));
        Assert.True(new FileInfo(outPath).Length <= 60 * 1024, $"{new FileInfo(outPath).Length} bytes");
    }

    [Fact]
    public void ResizeCropRotate()
    {
        using var t = new TempDir();
        var src = Fixtures.Png(t);
        Assert.Equal((320, 240), ImageEngine.Size(ImageEngine.Resize(src, new ImageResizeOptions(50))));
        Assert.Equal((320, 240), ImageEngine.Size(ImageEngine.Resize(src, new ImageResizeOptions(MaxWidth: 1000, MaxHeight: 240))));
        Assert.Equal((480, 480), ImageEngine.Size(ImageEngine.Crop(src, null, "1:1")));
        Assert.Equal((100, 50), ImageEngine.Size(ImageEngine.Crop(src, new PixelRect(10, 10, 100, 50), null)));
        Assert.Equal((480, 640), ImageEngine.Size(ImageEngine.Rotate(src, RotateOp.Clockwise90)));
    }

    [Fact]
    public void BackgroundCollageRedactAdjust()
    {
        using var t = new TempDir();
        var a = Fixtures.Png(t, "a.png");
        var b = Fixtures.Png(t, "b.png", 300, 600);
        var bg = ImageEngine.AddBackground(a, new BackgroundOptions(Aspect: "1:1"));
        var (w, h) = ImageEngine.Size(bg);
        Assert.Equal(w, h);
        Assert.True(File.Exists(ImageEngine.Collage([a, b, a], new CollageOptions(Cell: 200))));
        Assert.True(File.Exists(ImageEngine.Collage([a, b], new CollageOptions("row", 200))));
        Assert.True(File.Exists(ImageEngine.Redact(a, [new PixelRect(0, 0, 100, 100)], RedactMode.Pixelate)));
        Assert.True(File.Exists(ImageEngine.Adjust(a, new AdjustOptions(Brightness: 10, Saturation: 140, Sepia: true))));
        Assert.True(File.Exists(ImageEngine.StripMetadata(a)));
    }

    [Fact]
    public void ImagesToPdf()
    {
        using var t = new TempDir();
        var a = Fixtures.Png(t, "a.png");
        var b = Fixtures.Png(t, "b.png", 300, 600);
        var pdf = t.File("out.pdf");
        ImageEngine.ToPdf([a, b], pdf);
        Assert.Equal(2, PdfEngine.PageCount(pdf));
    }
}

public class PdfTests
{
    [Fact]
    public async Task RendersPagesWithWindowsApi()
    {
        using var t = new TempDir();
        var pdf = Fixtures.TextPdf(t, pages: 2);
        var outs = await PdfEngine.RenderPagesAsync(pdf, "png", 72, null, default);
        var dir = Assert.Single(outs);
        Assert.Equal(2, Directory.GetFiles(dir, "*.png").Length);
        var single = Fixtures.TextPdf(t, "one.pdf", pages: 1);
        var jpg = Assert.Single(await PdfEngine.RenderPagesAsync(single, "jpg", 100, null, default));
        Assert.EndsWith(".jpg", jpg);
    }

    [Fact]
    public void TextMarkdownHtmlTable()
    {
        using var t = new TempDir();
        var pdf = Fixtures.TextPdf(t);
        Assert.Contains("paragrafo", PdfEngine.ExtractText(pdf));
        var md = PdfEngine.ToMarkdown(pdf);
        Assert.Contains("# Titulo da pagina 1", md);
        Assert.Contains("<h", PdfEngine.ToHtml(pdf));
        Assert.Contains(PdfEngine.ToTable(pdf), r => r.Length == 2 && r[0] == "Nome" && r[1] == "Valor");
    }

    [Fact]
    public void MergeSplitOrganizeMetadata()
    {
        using var t = new TempDir();
        var a = Fixtures.TextPdf(t, "a.pdf", 2);
        var b = Fixtures.TextPdf(t, "b.pdf", 3);
        var merged = PdfEngine.Merge([a, b]);
        Assert.Equal(5, PdfEngine.PageCount(merged));
        var dir = PdfEngine.Split(b);
        Assert.Equal(3, Directory.GetFiles(dir, "*.pdf").Length);
        var org = PdfEngine.Organize(b, [new PageOp(2, 90), new PageOp(0, 0), new PageOp(0, 0)]);
        Assert.Equal(3, PdfEngine.PageCount(org));
        Assert.Equal(2, PdfEngine.PageCount(PdfEngine.ExtractPages(b, "2-")));
        Assert.Equal([0, 1, 2, 4], PdfEngine.ParsePageRanges("1-3, 5", 5));
        var edited = PdfEngine.WriteMetadata(a, new PdfMetadata("Novo", "Eu", "", ""));
        Assert.Equal("Novo", PdfEngine.ReadMetadata(edited).Title);
        var stripped = PdfEngine.WriteMetadata(a, null);
        Assert.Equal("", PdfEngine.ReadMetadata(stripped).Title);
    }

    [Fact]
    public async Task CompressNeverGrows()
    {
        using var t = new TempDir();
        var imgs = Enumerable.Range(0, 3).Select(i => ImageEngine.Convert(Fixtures.Png(t, $"p{i}.png", 2400, 1800), "jpg")).ToList();
        var pdf = t.File("fotos.pdf");
        ImageEngine.ToPdf(imgs, pdf);
        var outPath = await PdfEngine.CompressAsync(pdf, PdfCompressLevel.Smallest, default);
        Assert.True(new FileInfo(outPath).Length <= new FileInfo(pdf).Length);
        Assert.Equal(3, PdfEngine.PageCount(outPath));
    }

    [Fact]
    public async Task CompressShrinksLosslessImages()
    {
        using var t = new TempDir();
        var png = Fixtures.Png(t, "shot.png", 2400, 1600);
        var pdf = t.File("screens.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            using var img = XImage.FromFile(png); // PNG is embedded losslessly (Flate)
            g.DrawImage(img, 20, 20, page.Width.Point - 40, (page.Width.Point - 40) * 1600 / 2400);
            doc.Save(pdf);
        }
        var outPath = await PdfEngine.CompressAsync(pdf, PdfCompressLevel.Balanced, default);
        Assert.True(new FileInfo(outPath).Length < new FileInfo(pdf).Length / 2,
            $"{new FileInfo(pdf).Length} -> {new FileInfo(outPath).Length}");
        Assert.Single(await PdfEngine.RenderPagesAsync(outPath, "png", 50, null, default));
    }

    [Fact]
    public async Task ConverterRoutesPdf()
    {
        using var t = new TempDir();
        var pdf = Fixtures.TextPdf(t);
        foreach (var target in new[] { "txt", "md", "html", "xlsx" })
        {
            var outs = await Converter.ConvertAsync(pdf, target, null, default);
            Assert.True(File.Exists(Assert.Single(outs)));
        }
    }
}

public class OtherFormatTests
{
    [Fact]
    public void Subtitles()
    {
        using var t = new TempDir();
        var srt = t.File("a.srt");
        File.WriteAllText(srt, "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Olá</i> mundo\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,000\r\nSegunda\r\nlinha\r\n");
        var vtt = SubtitleEngine.Convert(srt, "vtt");
        var text = File.ReadAllText(vtt);
        Assert.StartsWith("WEBVTT", text);
        Assert.Contains("00:00:01.000 --> 00:00:02.500", text);
        var back = SubtitleEngine.Convert(vtt, "srt");
        Assert.Equal(2, SubtitleEngine.Parse(File.ReadAllText(back)).Count);
        Assert.Contains("Olá mundo", File.ReadAllText(SubtitleEngine.Convert(srt, "txt")));
    }

    [Fact]
    public async Task Spreadsheets()
    {
        using var t = new TempDir();
        var csv = t.File("dados.csv");
        File.WriteAllText(csv, "nome;valor;cep\nAna;10,5;007\n\"Bia; B\";3;123\n");
        var xlsx = Assert.Single(await SpreadsheetEngine.ConvertAsync(csv, "xlsx", default));
        var rows = SpreadsheetEngine.ReadTable(xlsx);
        Assert.Equal("Bia; B", rows[2][0]);
        Assert.Equal("007", rows[1][2]);
        var tsv = Assert.Single(await SpreadsheetEngine.ConvertAsync(xlsx, "tsv", default));
        Assert.Contains("Ana\t10,5\t007", File.ReadAllText(tsv));
    }

    [Fact]
    public void Archives()
    {
        using var t = new TempDir();
        var folder = Path.Combine(t.Path, "pasta");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "bbb");
        var zip = ArchiveEngine.ZipItems([folder], null, default);
        using (var z = ZipFile.OpenRead(zip)) Assert.Contains(z.Entries, e => e.FullName == "pasta/sub/b.txt");

        var tgz = ArchiveEngine.Convert(zip, "tar.gz", null, default);
        Assert.EndsWith(".tar.gz", tgz);
        var extracted = ArchiveEngine.Extract(tgz, null, default);
        Assert.True(File.Exists(Path.Combine(extracted, "sub", "b.txt")), extracted);
        var tar = ArchiveEngine.Convert(zip, "tar", null, default);
        var ex2 = ArchiveEngine.Extract(tar, null, default);
        Assert.True(File.Exists(Path.Combine(ex2, "a.txt")));
    }

    [Fact]
    public void ZipHandlesOpenFilesDuplicatesAndCancel()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.File("d1"));
        Directory.CreateDirectory(t.File("d2"));
        var one = Path.Combine(t.Path, "d1", "same.txt");
        var two = Path.Combine(t.Path, "d2", "same.txt");
        File.WriteAllText(one, "1");
        File.WriteAllText(two, "2");

        // A file another program keeps open for writing (like a log) still goes in.
        using (new FileStream(one, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var zip = ArchiveEngine.ZipItems([one, two], null, default);
            using var z = ZipFile.OpenRead(zip);
            Assert.Equal(["same.txt", "same (2).txt"], z.Entries.Select(e => e.FullName).ToArray());
            using var r = new StreamReader(z.GetEntry("same (2).txt")!.Open());
            Assert.Equal("2", r.ReadToEnd());
        }

        // A cancelled job leaves no broken ZIP behind.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveEngine.ZipItems([t.File("d2")], null, cts.Token));
        Assert.False(File.Exists(t.File("d2.zip")));
    }

    [Fact]
    public void ZipSlipIsBlocked()
    {
        using var t = new TempDir();
        var zip = t.File("evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("../../escaped.txt").Open());
            w.Write("x");
        }
        var dest = ArchiveEngine.Extract(zip, null, default);
        Assert.False(File.Exists(Path.Combine(t.Path, "..", "escaped.txt")));
        Assert.True(Directory.Exists(dest));
    }
}

public class NativeDocsTests
{
    static NativeDocsTests() => OfficeEngine.Disabled = true;

    private const string Md = """
        # Relatório

        Texto com **negrito** e *itálico*: ção.

        ## Lista

        - Um
        - Dois
          - Sub

        1. Primeiro
        2. Segundo

        | Nome | Valor |
        |------|-------|
        | Ana  | 10    |

        """;

    private static string WriteMd(TempDir t)
    {
        var path = t.File("doc.md");
        File.WriteAllText(path, Md);
        return path;
    }

    [Fact]
    public async Task MarkdownToDocxAndBack()
    {
        using var t = new TempDir();
        var docx = await NativeDocs.ConvertAsync(WriteMd(t), "docx");
        var html = NativeDocs.ReadDocx(docx);
        Assert.Contains("<h1>Relatório</h1>", html.Body);
        Assert.Contains("<strong>negrito</strong>", html.Body);
        Assert.Contains("<ol>", html.Body);
        Assert.Contains("<table>", html.Body);
        var md = File.ReadAllText(await NativeDocs.ConvertAsync(docx, "md"));
        Assert.Contains("# Relatório", md);
        Assert.Contains("- Um", md);
        Assert.Contains("1. Primeiro", md);
        Assert.Contains("| Ana | 10 |", md);
    }

    [Fact]
    public async Task PlainTextKeepsListsAndTables()
    {
        using var t = new TempDir();
        var txt = File.ReadAllText(await NativeDocs.ConvertAsync(WriteMd(t), "txt"));
        Assert.Contains("- Um\n- Dois\n  - Sub", txt.Replace("\r\n", "\n"));
        Assert.Contains("1. Primeiro", txt);
        Assert.Contains("Ana\t10", txt);
    }

    [Fact]
    public async Task EpubRoundTrip()
    {
        using var t = new TempDir();
        var md = t.File("livro.md");
        var img = Fixtures.Png(t, "capa.png", 200, 100);
        File.WriteAllText(md, "# Capítulo 1\n\nTexto um.\n\n![capa](capa.png)\n\n# Capítulo 2\n\nTexto dois.\n");
        var epub = await NativeDocs.ConvertAsync(md, "epub");
        using (var zip = ZipFile.OpenRead(epub))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("OEBPS/text/")));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("OEBPS/images/"));
        }
        var back = NativeDocs.ReadEpub(epub);
        Assert.Contains("Texto dois", back.Body);
        Assert.Contains("data:image/png;base64", back.Body);
    }

    [Fact]
    public void ReadsRtf()
    {
        using var t = new TempDir();
        var rtf = t.File("a.rtf");
        File.WriteAllText(rtf, @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0 {\b Titulo}\par Texto {\i italico} com acentua\'e7\'e3o.\par}");
        var d = NativeDocs.ReadRtf(rtf);
        Assert.Contains("Titulo", d.Body);
        Assert.Contains("acentuação", d.Body);
    }

    [Fact]
    public void ReadsOdt()
    {
        using var t = new TempDir();
        var odt = t.File("a.odt");
        using (var zip = ZipFile.Open(odt, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("mimetype").Open())) w.Write("application/vnd.oasis.opendocument.text");
            using (var w = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            {
                w.Write("""
                    <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                        xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                        xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
                        xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
                        xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0">
                      <office:automatic-styles>
                        <style:style style:name="T1" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>
                      </office:automatic-styles>
                      <office:body><office:text>
                        <text:h text:outline-level="1">Título ODT</text:h>
                        <text:p>Um <text:span text:style-name="T1">forte</text:span> texto.</text:p>
                        <text:list><text:list-item><text:p>Item A</text:p></text:list-item></text:list>
                        <table:table><table:table-row><table:table-cell><text:p>X</text:p></table:table-cell><table:table-cell><text:p>Y</text:p></table:table-cell></table:table-row></table:table>
                      </office:text></office:body>
                    </office:document-content>
                    """);
            }
        }
        var d = NativeDocs.ReadOdt(odt);
        Assert.Equal("Título ODT", d.Title);
        Assert.Contains("<strong>forte</strong>", d.Body);
        Assert.Contains("<li>Item A</li>", d.Body);
        Assert.Contains("<td>X</td><td>Y</td>", d.Body);
    }

    [Fact]
    public async Task PdfToDocxIsNative()
    {
        using var t = new TempDir();
        var pdf = Fixtures.TextPdf(t);
        var docx = Assert.Single(await Converter.ConvertAsync(pdf, "docx", null, default));
        Assert.Contains("Titulo da pagina 1", NativeDocs.ReadDocx(docx).Body);
        Assert.Null(Converter.RequiredTool(pdf, "docx"));
        Assert.Null(Converter.RequiredTool(t.File("x.docx"), "md"));
        Assert.Null(Converter.RequiredTool(t.File("x.rtf"), "txt"));
    }
}

public class CustomActionTests
{
    [Fact]
    public void ExpandsPlaceholders()
    {
        using var t = new TempDir();
        var f = t.File("foto final.jpg");
        File.WriteAllText(f, "x");
        var outs = new List<string>();
        var s = CustomActionRunner.Expand("\"{path}\" {name} {ext} {ask:Largura} \"{out:png}\"", f, [f], new Dictionary<string, string> { ["Largura"] = "800" }, outs);
        Assert.Equal($"\"{f}\" foto final jpg 800 \"{t.File("foto final - saída.png")}\"", s);
        Assert.Single(outs);
        Assert.Equal(["Largura"], CustomActionRunner.Questions(new CustomAction { Arguments = "{ask:Largura} {ask:Largura}" }));
    }

    [Fact]
    public void MatchesExtensions()
    {
        var a = new CustomAction { Extensions = "jpg, .png" };
        Assert.True(a.Matches(["x.JPEG", "y.png"]));
        Assert.False(a.Matches(["x.gif"]));
        Assert.True(new CustomAction { Extensions = "folder" }.Matches([Path.GetTempPath()]));
    }

    [Fact]
    public async Task RunsCommandAndCollectsOutput()
    {
        using var t = new TempDir();
        var f = t.File("in.txt");
        File.WriteAllText(f, "hello");
        var a = new CustomAction { Command = "cmd.exe", Arguments = "/c copy \"{path}\" \"{out:bak}\"", WaitForExit = true };
        var outs = await CustomActionRunner.RunAsync(a, [f], new Dictionary<string, string>(), null, default);
        Assert.Equal("hello", File.ReadAllText(Assert.Single(outs)));
    }
}

public class TimeTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03.5", 3723.5)]
    [InlineData("1m30s", 90)]
    public void ParsesTimes(string text, double seconds)
    {
        Assert.True(MediaEngine.TryParseTime(text, out var ts));
        Assert.Equal(seconds, ts.TotalSeconds, 3);
    }

    [Fact]
    public void ParsesLists()
    {
        Assert.Equal(3, MediaEngine.ParseTimes("0:30, 1:15; 2:00").Count);
        Assert.Equal(2, MediaEngine.ParseRanges("0:10-0:12, 1:05-1:06").Count);
        Assert.Throws<FormatException>(() => MediaEngine.ParseRanges("0:12-0:10"));
    }
}

public class MediaTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public MediaTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        // Prefer the FFmpeg the app ships with, so the tests check what users get.
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var bundled = Path.Combine(repo, "src", "WinKame", "tools", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled))
        {
            ToolLocator.Overrides[ExternalTool.FFmpeg] = bundled;
            ToolLocator.Invalidate();
        }
    }

    private bool HasFfmpeg
    {
        get
        {
            var path = ToolLocator.Find(ExternalTool.FFmpeg);
            output.WriteLine(path is null ? "SKIPPED: FFmpeg not installed" : "Using FFmpeg: " + path);
            return path is not null;
        }
    }

    private static async Task<string> MakeClip(TempDir t)
    {
        var ff = ToolLocator.Require(ExternalTool.FFmpeg);
        var path = t.File("clip.mp4");
        await ProcessRunner.RunAsync(ff, ["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=4", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path], default);
        return path;
    }

    [Fact]
    public async Task VideoPipeline()
    {
        if (!HasFfmpeg) return; // FFmpeg is optional on the build machine.
        using var t = new TempDir();
        var clip = await MakeClip(t);
        var info = await MediaEngine.ProbeAsync(clip);
        Assert.True(info.HasVideo && info.HasAudio);
        Assert.Equal(320, info.Width);
        Assert.InRange(info.Duration.TotalSeconds, 3.5, 4.5);

        double last = 0;
        var mp3 = await MediaEngine.ConvertAsync(clip, "mp3", new Progress<double>(v => last = v), default);
        Assert.False((await MediaEngine.ProbeAsync(mp3)).HasVideo);
        Assert.True(File.Exists(await MediaEngine.ConvertAsync(clip, "gif", null, default)));
        Assert.True(File.Exists(await MediaEngine.ConvertAsync(clip, "webm", null, default)));
        var trimmed = await MediaEngine.TrimAsync(clip, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), false, null, default);
        Assert.InRange((await MediaEngine.ProbeAsync(trimmed)).Duration.TotalSeconds, 1.2, 1.8);
        var parts = await MediaEngine.SplitAsync(clip, [TimeSpan.FromSeconds(2)], null, default);
        Assert.Equal(2, Directory.GetFiles(Assert.Single(parts)).Length);
        var joined = await MediaEngine.JoinAsync([clip, trimmed], null, default);
        Assert.InRange((await MediaEngine.ProbeAsync(joined)).Duration.TotalSeconds, 5, 6.2);
        Assert.False((await MediaEngine.ProbeAsync(await MediaEngine.MuteAsync(clip, null, default))).HasAudio);
        Assert.True(File.Exists(await MediaEngine.SpeedAsync(clip, 2, null, default)));
        Assert.True(File.Exists(Assert.Single(await MediaEngine.SnapshotsAsync(clip, [TimeSpan.FromSeconds(1)], null, default))));
        Assert.Equal(240, (await MediaEngine.ProbeAsync(await MediaEngine.CropAspectAsync(clip, "1:1", null, default))).Width);
        Assert.True(File.Exists(await MediaEngine.CompressVideoAsync(clip, CompressLevel.Strong, 0, null, default)));
        Assert.True((await MediaEngine.FrameAsync(clip, TimeSpan.FromSeconds(1), 160, default)).Length > 100);
    }

    [Fact]
    public async Task OtherVideoFormatsAndTools()
    {
        if (!HasFfmpeg) return;
        using var t = new TempDir();
        var clip = await MakeClip(t);
        foreach (var target in new[] { "mov", "mkv", "avi", "wmv", "m4a", "wav" })
        {
            var outPath = await MediaEngine.ConvertAsync(clip, target, null, default);
            var info = await MediaEngine.ProbeAsync(outPath);
            Assert.True(info.HasAudio, target);
            Assert.Equal(!MediaEngine.IsAudioExt(target), info.HasVideo);
        }
        var rotated = await MediaEngine.RotateVideoAsync(clip, RotateOp.Clockwise90, null, default);
        var r = await MediaEngine.ProbeAsync(rotated);
        Assert.Equal((240, 320), (r.Width, r.Height));
        Assert.True(File.Exists(await MediaEngine.StripMetadataAsync(clip, null, default)));

        // Animated GIF in, MP4 out, through the image converter route.
        var gif = await MediaEngine.ConvertAsync(clip, "gif", null, default);
        var mp4 = Assert.Single(await Converter.ConvertAsync(gif, "mp4", null, default));
        Assert.True((await MediaEngine.ProbeAsync(mp4)).HasVideo);
    }

    [Fact]
    public async Task AudioPipeline()
    {
        if (!HasFfmpeg) return;
        using var t = new TempDir();
        var clip = await MakeClip(t);
        var wav = await MediaEngine.ConvertAsync(clip, "wav", null, default);
        foreach (var target in new[] { "flac", "ogg", "opus", "m4a", "aiff", "wma" })
        {
            Assert.True(File.Exists(await MediaEngine.ConvertAsync(wav, target, null, default)), target);
        }
        Assert.EndsWith(".mp3", await MediaEngine.CompressAudioAsync(wav, 96, null, default));
        Assert.True(File.Exists(await MediaEngine.NormalizeAsync(wav, -16, null, default)));
        Assert.True(File.Exists(await MediaEngine.ChannelsAsync(wav, 1, null, default)));
        Assert.True(File.Exists(await MediaEngine.BleepAsync(wav, [(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))], null, default)));
        Assert.True(File.Exists(await MediaEngine.VisualizeAsync(wav, VisualizeShape.Square, "#FF8A00", "black", null, default)));
        var mp3 = await MediaEngine.ConvertAsync(wav, "mp3", null, default);
        var tagged = await MediaEngine.SetTagsAsync(mp3, new Dictionary<string, string> { ["artist"] = "Teste" }, false, null, default);
        Assert.Equal("Teste", (await MediaEngine.ProbeAsync(tagged)).Tags["artist"]);
        Assert.True((await MediaEngine.WaveformAsync(wav, 400, 80, "#FF8A00", default)).Length > 100);
    }
}
