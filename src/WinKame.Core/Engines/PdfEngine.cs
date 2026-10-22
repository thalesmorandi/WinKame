// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Globalization;
using System.Net;
using System.Text;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Windows.Storage;
using Windows.Storage.Streams;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;
using WinPdf = Windows.Data.Pdf;

namespace WinKame.Core.Engines;

public readonly record struct PageOp(int Source, int Rotation);

public enum PdfCompressLevel { HighQuality, Balanced, Smallest }

public sealed record PdfMetadata(string Title, string Author, string Subject, string Keywords);

public static partial class PdfEngine
{
    // ---------- page count / rendering (Windows.Data.Pdf, no extra software) ----------

    public static async Task<WinPdf.PdfDocument> OpenForRenderAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        return await WinPdf.PdfDocument.LoadFromFileAsync(file);
    }

    public static int PageCount(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>Renders one page to PNG bytes. Width in pixels (0 = use dpi).</summary>
    public static async Task<byte[]> RenderPageAsync(WinPdf.PdfDocument doc, int index, int width = 0, double dpi = 150)
    {
        using var page = doc.GetPage((uint)index);
        var opts = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, width > 0 ? width : Math.Round(page.Size.Width * dpi / 96.0)),
        };
        using var ras = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ras, opts);
        ras.Seek(0);
        using var s = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>Every page as PNG/JPG. One page gives one file; more go into a new folder.</summary>
    public static async Task<IReadOnlyList<string>> RenderPagesAsync(string pdf, string format, double dpi, IProgress<double>? progress, CancellationToken ct, string? nameSource = null)
    {
        format = Formats.Canonical(format);
        var doc = await OpenForRenderAsync(pdf);
        var count = (int)doc.PageCount;
        var source = nameSource ?? pdf;
        var stem = OutputPaths.StemOf(source);
        var dir = count > 1 ? OutputPaths.FolderFor(source, "páginas") : OutputPaths.DirFor(source);
        var outputs = new List<string>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var png = await RenderPageAsync(doc, i, 0, dpi);
            var outPath = count > 1 ? OutputPaths.Unique(dir, $"{stem} - {i + 1:000}", format) : OutputPaths.Unique(dir, stem, format);
            if (format == "png")
            {
                await File.WriteAllBytesAsync(outPath, png, ct);
            }
            else
            {
                using var img = new MagickImage(png);
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaOption.Remove);
                img.Quality = 92;
                img.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
                img.Write(outPath, MagickFormat.Jpeg);
            }
            outputs.Add(outPath);
            progress?.Report((i + 1) / (double)count);
        }
        return count > 1 ? [dir] : outputs;
    }

    // ---------- text ----------

    public static string ExtractText(string pdf)
    {
        using var doc = PigDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(ContentOrderTextExtractor.GetText(page).TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private enum ListKind { None, Bullet, Ordered }

    /// <summary>A paragraph, list item, or (when <see cref="Rows"/> is set) a table.</summary>
    private sealed record Block(string Text, double Size, ListKind List, double? RowY = null, double Left = 0, List<string[]>? Rows = null)
    {
        public bool Bullet => List == ListKind.Bullet;
    }

    /// <summary>Merges runs of table cells (text sharing a baseline with other columns) into table blocks.</summary>
    private static List<Block> GroupTables(List<Block> page)
    {
        var result = new List<Block>();
        var i = 0;
        while (i < page.Count)
        {
            if (page[i].RowY is null)
            {
                result.Add(page[i++]);
                continue;
            }
            var cells = new List<Block>();
            while (i < page.Count && page[i].RowY is not null) cells.Add(page[i++]);
            var rows = cells
                .GroupBy(c => Math.Round(c.RowY!.Value / 2))
                .OrderByDescending(g => g.Key)
                .Select(g => g.OrderBy(c => c.Left).Select(c => c.Text).ToArray())
                .ToList();
            result.Add(new Block("", cells.Average(c => c.Size), ListKind.None, Rows: rows));
        }
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{1,3})[.)]\s+")]
    private static partial System.Text.RegularExpressions.Regex NumberedRx();

    /// <summary>Paragraphs in reading order, with their average font size and list role.</summary>
    private static List<Block> Blocks(string pdf)
    {
        using var doc = PigDocument.Open(pdf);
        var blocks = new List<Block>();
        foreach (var page in doc.GetPages())
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
            if (words.Count == 0) continue;
            var segs = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(segs);
            // The page's text margin: list items sit to the right of it.
            var margin = segs.SelectMany(s => s.TextLines).Select(l => Math.Round(l.BoundingBox.Left)).GroupBy(x => x)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            var textRight = segs.SelectMany(s => s.TextLines).Max(l => l.BoundingBox.Right);

            // Lines that share a baseline with a line of another block are table cells.
            // Short text only, so two-column prose isn't mistaken for a table.
            var allLines = segs.SelectMany((s, si) => s.TextLines.Select(l => (Seg: si, Line: l)))
                .Where(a => a.Line.Text.Trim().Length is > 0 and <= 40)
                .ToList();
            var cellLines = allLines
                .Where(a => allLines.Any(o => o.Seg != a.Seg && Math.Abs(o.Line.BoundingBox.Bottom - a.Line.BoundingBox.Bottom) < 1.5))
                .Select(a => a.Line)
                .ToHashSet();
            var pageBlocks = new List<Block>();

            foreach (var b in ordered)
            {
                var group = new List<string>();
                double groupSize = 0, groupLeft = 0;
                var kind = ListKind.None;

                void Flush()
                {
                    var text = string.Join(" ", group).Trim();
                    group.Clear();
                    if (text.Length == 0) return;
                    var k = kind;
                    if (text.Length > 1 && "•◦▪‣-–*·".Contains(text[0]) && char.IsWhiteSpace(text[1]))
                    {
                        text = text[1..].Trim();
                        k = ListKind.Bullet;
                    }
                    else if (NumberedRx().Match(text) is { Success: true } m)
                    {
                        text = text[m.Length..];
                        k = ListKind.Ordered;
                    }
                    pageBlocks.Add(new Block(text, groupSize, k));
                }

                double prevRight = 0;
                foreach (var line in b.TextLines)
                {
                    var letters = line.Words.SelectMany(w => w.Letters).ToList();
                    var size = letters.Count > 0 ? letters.Average(l => l.PointSize) : groupSize;
                    var text = line.Text.Trim();
                    var left = line.BoundingBox.Left;
                    if (cellLines.Contains(line))
                    {
                        Flush();
                        pageBlocks.Add(new Block(text, size, ListKind.None, line.BoundingBox.Bottom, left));
                        prevRight = textRight;
                        continue;
                    }
                    var startsItem = text.Length > 0 && ("•◦▪‣".Contains(text[0]) || NumberedRx().IsMatch(text));
                    // A line that stops well before the page's right edge ends its paragraph.
                    var endedEarly = prevRight < textRight - (textRight - margin) * 0.2;
                    if (group.Count > 0 && (Math.Abs(size - groupSize) > groupSize * 0.15 || startsItem || Math.Abs(left - groupLeft) > 2.5 || endedEarly))
                    {
                        Flush();
                    }
                    if (group.Count == 0)
                    {
                        groupSize = size;
                        groupLeft = left;
                        // Indented lines are list items whose bullet was drawn as a shape, not text.
                        // Only a modest indent counts; text far to the right is a table column.
                        kind = left > margin + 8 && left < margin + 72 ? ListKind.Bullet : ListKind.None;
                    }
                    group.Add(text);
                    prevRight = line.BoundingBox.Right;
                }
                Flush();
            }
            blocks.AddRange(GroupTables(pageBlocks));
        }
        return blocks;
    }

    private static double BodySize(List<Block> blocks)
    {
        var sizes = blocks.SelectMany(b => Enumerable.Repeat(Math.Round(b.Size, 1), Math.Max(1, b.Text.Length / 20))).Order().ToList();
        return sizes.Count == 0 ? 10 : sizes[sizes.Count / 2];
    }

    public static string ToMarkdown(string pdf)
    {
        var blocks = Blocks(pdf);
        var body = BodySize(blocks);
        var sb = new StringBuilder();
        var number = 0;
        var prev = ListKind.None;
        foreach (var b in blocks)
        {
            var isHeading = b.Size >= body * 1.3 && b.Text.Length < 160;
            number = b.List == ListKind.Ordered ? number + 1 : 0;
            // Markdown needs a blank line where a list starts, ends or changes type.
            if (b.List != prev && prev != ListKind.None) sb.AppendLine();
            prev = b.List;
            if (b.Rows is { Count: > 0 } rows)
            {
                var cols = rows.Max(r => r.Length);
                string Row(string[] r) => "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(c => c < r.Length ? r[c].Replace("|", "\\|") : "")) + " |";
                sb.AppendLine(Row(rows[0]));
                sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", cols)));
                foreach (var r in rows.Skip(1)) sb.AppendLine(Row(r));
                sb.AppendLine();
                continue;
            }
            if (b.List == ListKind.Ordered) sb.Append(number).Append(". ").AppendLine(b.Text);
            else if (b.Bullet) sb.Append("- ").AppendLine(b.Text);
            else if (isHeading) sb.Append(b.Size >= body * 1.8 ? "# " : "## ").AppendLine(b.Text).AppendLine();
            else sb.AppendLine(b.Text).AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string ToHtml(string pdf) => NativeDocs.Standalone(ToHtmlDoc(pdf));

    /// <summary>The PDF's text as an HTML document (headings by font size, bullets as lists).</summary>
    public static HtmlDoc ToHtmlDoc(string pdf)
    {
        var blocks = Blocks(pdf);
        var body = BodySize(blocks);
        var sb = new StringBuilder();
        string? openList = null;
        foreach (var b in blocks)
        {
            var t = WebUtility.HtmlEncode(b.Text);
            var tag = b.List switch { ListKind.Bullet => "ul", ListKind.Ordered => "ol", _ => null };
            if (openList is not null && openList != tag)
            {
                sb.AppendLine($"</{openList}>");
                openList = null;
            }
            if (tag is not null)
            {
                if (openList is null) { sb.AppendLine($"<{tag}>"); openList = tag; }
                sb.AppendLine($"<li>{t}</li>");
                continue;
            }
            if (b.Rows is { Count: > 0 } rows)
            {
                sb.AppendLine("<table>");
                for (var r = 0; r < rows.Count; r++)
                {
                    var cell = r == 0 ? "th" : "td";
                    sb.Append("<tr>");
                    foreach (var c in rows[r]) sb.Append($"<{cell}>{WebUtility.HtmlEncode(c)}</{cell}>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</table>");
                continue;
            }
            if (b.Size >= body * 1.8 && b.Text.Length < 160) sb.AppendLine($"<h1>{t}</h1>");
            else if (b.Size >= body * 1.3 && b.Text.Length < 160) sb.AppendLine($"<h2>{t}</h2>");
            else sb.AppendLine($"<p>{t}</p>");
        }
        if (openList is not null) sb.AppendLine($"</{openList}>");
        var title = blocks.FirstOrDefault(b => b.Size >= body * 1.3 && b.Text.Length < 160)?.Text ?? OutputPaths.StemOf(pdf);
        return new HtmlDoc(title, sb.ToString());
    }

    /// <summary>Rows of cells, guessed from word positions on each page.</summary>
    public static List<string[]> ToTable(string pdf)
    {
        using var doc = PigDocument.Open(pdf);
        var rows = new List<string[]>();
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) continue;
            var avgH = words.Average(w => w.BoundingBox.Height);
            var lines = new List<List<Word>>();
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
            {
                var line = lines.FirstOrDefault(l => Math.Abs(l[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < avgH * 0.5);
                if (line is null) lines.Add([w]);
                else line.Add(w);
            }
            foreach (var line in lines)
            {
                var sorted = line.OrderBy(w => w.BoundingBox.Left).ToList();
                var charW = sorted.Average(w => w.BoundingBox.Width / Math.Max(1, w.Text.Length));
                var cells = new List<string>();
                var current = new StringBuilder(sorted[0].Text);
                for (var i = 1; i < sorted.Count; i++)
                {
                    var gap = sorted[i].BoundingBox.Left - sorted[i - 1].BoundingBox.Right;
                    if (gap > charW * 2.2)
                    {
                        cells.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(' ');
                    }
                    current.Append(sorted[i].Text);
                }
                cells.Add(current.ToString());
                rows.Add([.. cells]);
            }
            rows.Add([]);
        }
        return rows;
    }

    // ---------- page operations (PDFsharp) ----------

    public static string Merge(IReadOnlyList<string> pdfs)
    {
        using var outDoc = new SharpDocument();
        foreach (var f in pdfs)
        {
            using var inDoc = PdfReader.Open(f, PdfDocumentOpenMode.Import);
            foreach (var p in inDoc.Pages) outDoc.AddPage(p);
        }
        var outPath = OutputPaths.Unique(OutputPaths.DirFor(pdfs[0]), $"{OutputPaths.StemOf(pdfs[0])} - {T("juntos")}", "pdf");
        outDoc.Save(outPath);
        return outPath;
    }

    public static string Split(string pdf, IProgress<double>? progress = null)
    {
        using var inDoc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var dir = OutputPaths.FolderFor(pdf, "páginas");
        var stem = OutputPaths.StemOf(pdf);
        for (var i = 0; i < inDoc.PageCount; i++)
        {
            using var one = new SharpDocument();
            one.AddPage(inDoc.Pages[i]);
            one.Save(Path.Combine(dir, $"{stem} - {i + 1:000}.pdf"));
            progress?.Report((i + 1) / (double)inDoc.PageCount);
        }
        return dir;
    }

    public static string Organize(string pdf, IReadOnlyList<PageOp> ops, string suffix = "organizado")
    {
        if (ops.Count == 0) throw new InvalidOperationException(T("O PDF precisa de pelo menos uma página."));
        using var inDoc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        using var outDoc = new SharpDocument();
        foreach (var op in ops)
        {
            var added = outDoc.AddPage(inDoc.Pages[op.Source]);
            added.Rotate = (((added.Rotate + op.Rotation) % 360) + 360) % 360;
        }
        var outPath = OutputPaths.For(pdf, "pdf", suffix);
        outDoc.Save(outPath);
        return outPath;
    }

    public static string RotateAll(string pdf, int degrees)
    {
        var count = PageCount(pdf);
        return Organize(pdf, Enumerable.Range(0, count).Select(i => new PageOp(i, degrees)).ToList(), "girado");
    }

    /// <summary>"1-3, 5, 8-" (1-based) into page indices.</summary>
    public static List<int> ParsePageRanges(string text, int count)
    {
        var result = new List<int>();
        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOf('-');
            int a, b;
            if (dash < 0)
            {
                if (!int.TryParse(part, out a)) throw new FormatException(T("Página inválida: {0}", part));
                b = a;
            }
            else
            {
                var left = part[..dash];
                var right = part[(dash + 1)..];
                a = left.Length == 0 ? 1 : int.Parse(left);
                b = right.Length == 0 ? count : int.Parse(right);
            }
            if (a < 1 || b > count || a > b) throw new FormatException(T("Intervalo fora do documento (1–{0}): {1}", count, part));
            for (var i = a; i <= b; i++) result.Add(i - 1);
        }
        return result;
    }

    public static string ExtractPages(string pdf, string ranges)
    {
        var count = PageCount(pdf);
        var pages = ParsePageRanges(ranges, count);
        return Organize(pdf, pages.Select(i => new PageOp(i, 0)).ToList(), "páginas");
    }

    public static PdfMetadata ReadMetadata(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return new PdfMetadata(doc.Info.Title ?? "", doc.Info.Author ?? "", doc.Info.Subject ?? "", doc.Info.Keywords ?? "");
    }

    public static string WriteMetadata(string pdf, PdfMetadata? meta)
    {
        var outPath = OutputPaths.For(pdf, "pdf", meta is null ? "sem metadados" : "editado");
        File.Copy(pdf, outPath);
        using (var doc = PdfReader.Open(outPath, PdfDocumentOpenMode.Modify))
        {
            if (meta is null)
            {
                doc.Info.Elements.Clear();
                doc.Internals.Catalog.Elements.Remove("/Metadata");
            }
            else
            {
                doc.Info.Title = meta.Title;
                doc.Info.Author = meta.Author;
                doc.Info.Subject = meta.Subject;
                doc.Info.Keywords = meta.Keywords;
            }
            doc.Save(outPath);
        }
        return outPath;
    }

    // ---------- compression ----------

    public static async Task<string> CompressAsync(string pdf, PdfCompressLevel level, CancellationToken ct)
    {
        var outPath = OutputPaths.For(pdf, "pdf", "comprimido");
        var gs = ToolLocator.Find(ExternalTool.Ghostscript);
        if (gs is not null)
        {
            var preset = level switch
            {
                PdfCompressLevel.HighQuality => "/printer",
                PdfCompressLevel.Balanced => "/ebook",
                _ => "/screen",
            };
            await ProcessRunner.RunAsync(gs,
            [
                "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6", $"-dPDFSETTINGS={preset}",
                "-dNOPAUSE", "-dQUIET", "-dBATCH", "-dDetectDuplicateImages=true",
                $"-sOutputFile={outPath}", pdf,
            ], ct).ConfigureAwait(false);
        }
        else
        {
            CompressManaged(pdf, outPath, level);
        }

        // Never hand back something bigger than the original.
        if (new FileInfo(outPath).Length >= new FileInfo(pdf).Length)
        {
            File.Copy(pdf, outPath, overwrite: true);
        }
        return outPath;
    }

    /// <summary>
    /// Re-encodes embedded images: JPEGs at lower quality and size, and 8-bit lossless RGB/gray
    /// images (screenshots, PNGs) as JPEG when that is smaller. Masks are left alone.
    /// </summary>
    private static void CompressManaged(string pdf, string outPath, PdfCompressLevel level)
    {
        var (quality, maxDim) = level switch
        {
            PdfCompressLevel.HighQuality => (85u, 2600u),
            PdfCompressLevel.Balanced => (70u, 1800u),
            _ => (50u, 1200u),
        };
        File.Copy(pdf, outPath, overwrite: true);
        using var doc = PdfReader.Open(outPath, PdfDocumentOpenMode.Modify);
        var all = doc.Internals.GetAllObjects();

        // Soft masks are images too, but must stay lossless.
        var masks = new HashSet<PdfObjectID>();
        foreach (var obj in all)
        {
            if (obj is PdfDictionary d && d.Elements["/SMask"] is PdfReference r) masks.Add(r.ObjectID);
        }

        foreach (var obj in all)
        {
            if (obj is not PdfDictionary d || d.Stream is null) continue;
            if (d.Elements.GetName("/Subtype") != "/Image" || masks.Contains(d.Internals.ObjectID)) continue;
            if (d.Elements.GetBoolean("/ImageMask") || d.Elements.ContainsKey("/Decode")) continue;
            try
            {
                var original = d.Stream.Value;
                var origFilter = d.Elements["/Filter"];
                var origParms = d.Elements["/DecodeParms"];
                MagickImage img;
                if (IsJpegOnly(d.Elements["/Filter"]))
                {
                    if (d.Elements["/ColorSpace"] is PdfName { Value: "/DeviceCMYK" }) continue;
                    img = new MagickImage(original);
                }
                else
                {
                    var channels = Components(d.Elements["/ColorSpace"]);
                    if (channels is not (1 or 3) || d.Elements.GetInteger("/BitsPerComponent") != 8) continue;
                    if (!d.Stream.TryUncompress()) continue;
                    var raw = d.Stream.Value;
                    var w = (uint)d.Elements.GetInteger("/Width");
                    var h = (uint)d.Elements.GetInteger("/Height");
                    if (raw.Length < w * h * channels) { RestoreFiltered(d, original, origFilter, origParms); continue; }
                    // Tiny images (icons, rules) are cheaper left as they are.
                    if (w * h < 64 * 64) { RestoreFiltered(d, original, origFilter, origParms); continue; }
                    img = new MagickImage();
                    img.ReadPixels(raw, new PixelReadSettings(w, h, StorageType.Char, channels == 3 ? "RGB" : "I"));
                }
                using (img)
                {
                    if (img.Width > maxDim || img.Height > maxDim) img.Resize(maxDim, maxDim);
                    img.Strip();
                    img.Quality = quality;
                    var encoded = img.ToByteArray(MagickFormat.Jpeg);
                    if (encoded.Length >= original.Length)
                    {
                        // Not worth it: put the original (still compressed) bytes back.
                        if (!IsJpegOnly(origFilter)) RestoreFiltered(d, original, origFilter, origParms);
                        continue;
                    }
                    d.Stream.Value = encoded;
                    d.Elements["/Filter"] = new PdfName("/DCTDecode");
                    d.Elements.SetInteger("/Width", (int)img.Width);
                    d.Elements.SetInteger("/Height", (int)img.Height);
                    d.Elements.SetInteger("/BitsPerComponent", 8);
                    d.Elements.SetInteger("/Length", encoded.Length);
                    d.Elements.Remove("/DecodeParms");
                }
            }
            catch
            {
                // Leave images we can't decode untouched.
            }
        }
        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Save(outPath);
    }

    /// <summary>Color components of an image color space we can handle; 0 for anything else.</summary>
    private static int Components(PdfItem? cs)
    {
        if (cs is PdfReference r) cs = r.Value;
        return cs switch
        {
            PdfName { Value: "/DeviceRGB" } => 3,
            PdfName { Value: "/DeviceGray" } => 1,
            PdfArray a when a.Elements.Count == 2 && a.Elements[0] is PdfName { Value: "/ICCBased" } =>
                (a.Elements[1] is PdfReference ir ? ir.Value : a.Elements[1]) is PdfDictionary icc ? icc.Elements.GetInteger("/N") : 0,
            _ => 0,
        };
    }

    /// <summary>Puts back the original encoded bytes after TryUncompress replaced them.</summary>
    private static void RestoreFiltered(PdfDictionary d, byte[] original, PdfItem? filter, PdfItem? parms)
    {
        d.Stream.Value = original;
        d.Elements.SetInteger("/Length", original.Length);
        if (filter is not null) d.Elements["/Filter"] = filter;
        if (parms is not null) d.Elements["/DecodeParms"] = parms;
    }

    private static bool IsJpegOnly(PdfItem? filter) => filter switch
    {
        PdfName n => n.Value == "/DCTDecode",
        PdfArray a => a.Elements.Count == 1 && a.Elements[0] is PdfName n1 && n1.Value == "/DCTDecode",
        _ => false,
    };

    public static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
