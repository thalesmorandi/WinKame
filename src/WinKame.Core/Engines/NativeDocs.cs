// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using HtmlToOpenXml;
using Markdig;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace WinKame.Core.Engines;

/// <summary>A document reduced to HTML, the hub every native conversion goes through.</summary>
public sealed record HtmlDoc(string Title, string Body);

/// <summary>
/// Reads and writes DOCX, Markdown, HTML, TXT and EPUB without external programs.
/// Every input becomes HTML; every output is written from HTML.
/// </summary>
public static partial class NativeDocs
{
    public static IReadOnlySet<string> Readable { get; } = new HashSet<string> { "docx", "md", "txt", "html", "epub", "rtf", "odt" };

    static NativeDocs()
    {
        // RTF files use Windows code pages (1252 and friends).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
    public static IReadOnlySet<string> Writable { get; } = new HashSet<string> { "docx", "md", "txt", "html", "epub" };

    /// <summary>Prints a local HTML file to PDF. Set by the app (WebView2); null when no browser engine is available.</summary>
    public static Func<string, string, CancellationToken, Task>? HtmlFileToPdf { get; set; }

    public static bool CanPrintPdf => HtmlFileToPdf is not null;

    private static readonly HtmlParser Parser = new();

    // ====================================================================== reading

    public static HtmlDoc Read(string path)
    {
        var ext = Formats.Canonical(Formats.Ext(path));
        return ext switch
        {
            "docx" => ReadDocx(path),
            "md" => ReadMarkdown(path),
            "txt" => ReadText(path),
            "html" => ReadHtml(path),
            "epub" => ReadEpub(path),
            "rtf" => ReadRtf(path),
            "odt" => ReadOdt(path),
            _ => throw new NotSupportedException(T("Leitura nativa não suporta .{0}", ext)),
        };
    }

    // ---------- RTF ----------

    public static HtmlDoc ReadRtf(string path)
    {
        var html = RtfPipe.Rtf.ToHtml(File.ReadAllText(path, Encoding.Latin1));
        var doc = Parser.ParseDocument(html);
        return new HtmlDoc(OutputPaths.StemOf(path), doc.Body?.InnerHtml ?? html);
    }

    // ---------- ODT ----------

    private static readonly XNamespace OdOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace OdText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace OdTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace OdDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace OdStyle = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace OdFo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    public static HtmlDoc ReadOdt(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var content = XDocument.Load(Open(zip, "content.xml"));

        // Bold/italic/underline come from automatic styles referenced by name.
        var styles = new Dictionary<string, (bool Bold, bool Italic, bool Underline)>();
        var orderedLists = new HashSet<string>();
        foreach (var s in content.Descendants(OdStyle + "style"))
        {
            var tp = s.Element(OdStyle + "text-properties");
            if (s.Attribute(OdStyle + "name")?.Value is not { } name || tp is null) continue;
            styles[name] = (
                tp.Attribute(OdFo + "font-weight")?.Value == "bold",
                tp.Attribute(OdFo + "font-style")?.Value == "italic",
                tp.Attribute(OdStyle + "text-underline-style")?.Value is { } u && u != "none");
        }
        foreach (var ls in content.Descendants(OdText + "list-style"))
        {
            if (ls.Elements(OdText + "list-level-style-number").Any() && ls.Attribute(OdStyle + "name")?.Value is { } n) orderedLists.Add(n);
        }

        string? title = null;
        var sb = new StringBuilder();

        string Inline(XElement e)
        {
            var b = new StringBuilder();
            foreach (var node in e.Nodes())
            {
                if (node is XText t) { b.Append(Esc(t.Value)); continue; }
                if (node is not XElement x) continue;
                if (x.Name == OdText + "s") b.Append(' ', int.TryParse(x.Attribute(OdText + "c")?.Value, out var c) ? c : 1);
                else if (x.Name == OdText + "tab") b.Append("&emsp;");
                else if (x.Name == OdText + "line-break") b.Append("<br>");
                else if (x.Name == OdText + "a") b.Append($"<a href=\"{Esc(x.Attribute(XLink + "href")?.Value ?? "#")}\">{Inline(x)}</a>");
                else if (x.Name == OdText + "span")
                {
                    var inner = Inline(x);
                    if (x.Attribute(OdText + "style-name")?.Value is { } sn && styles.TryGetValue(sn, out var st))
                    {
                        if (st.Underline) inner = $"<u>{inner}</u>";
                        if (st.Italic) inner = $"<em>{inner}</em>";
                        if (st.Bold) inner = $"<strong>{inner}</strong>";
                    }
                    b.Append(inner);
                }
                else if (x.Name == OdDraw + "frame")
                {
                    var href = x.Element(OdDraw + "image")?.Attribute(XLink + "href")?.Value;
                    var entry = href is null ? null : zip.GetEntry(href);
                    if (entry is null) continue;
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    b.Append($"<img src=\"{DataUri(ms.ToArray(), MimeOf(entry.Name))}\" alt=\"\">");
                }
                else b.Append(Inline(x));
            }
            return b.ToString();
        }

        void Blocks(XElement parent, StringBuilder o)
        {
            foreach (var x in parent.Elements())
            {
                if (x.Name == OdText + "h")
                {
                    var lvl = Math.Clamp(int.TryParse(x.Attribute(OdText + "outline-level")?.Value, out var l) ? l : 1, 1, 6);
                    var text = Inline(x);
                    title ??= WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
                    o.Append($"<h{lvl}>{text}</h{lvl}>\n");
                }
                else if (x.Name == OdText + "p")
                {
                    var text = Inline(x);
                    if (text.Trim().Length > 0) o.Append($"<p>{text}</p>\n");
                }
                else if (x.Name == OdText + "list")
                {
                    var tag = x.Attribute(OdText + "style-name")?.Value is { } ln && orderedLists.Contains(ln) ? "ol" : "ul";
                    o.Append('<').Append(tag).Append(">\n");
                    foreach (var item in x.Elements(OdText + "list-item"))
                    {
                        var inner = new StringBuilder();
                        Blocks(item, inner);
                        // A single paragraph inside an item reads better without the <p>.
                        var li = Regex.Replace(inner.ToString().Trim(), @"^<p>(.*)</p>$", "$1", RegexOptions.Singleline);
                        o.Append("<li>").Append(li).Append("</li>\n");
                    }
                    o.Append("</").Append(tag).Append(">\n");
                }
                else if (x.Name == OdTable + "table")
                {
                    o.Append("<table>\n");
                    foreach (var row in x.Descendants(OdTable + "table-row"))
                    {
                        o.Append("<tr>");
                        foreach (var cell in row.Elements(OdTable + "table-cell"))
                        {
                            var span = cell.Attribute(OdTable + "number-columns-spanned")?.Value;
                            var inner = new StringBuilder();
                            Blocks(cell, inner);
                            var cellHtml = Regex.Replace(inner.ToString().Trim(), @"^<p>(.*)</p>$", "$1", RegexOptions.Singleline);
                            o.Append(span is null ? "<td>" : $"<td colspan=\"{span}\">").Append(cellHtml).Append("</td>");
                        }
                        o.Append("</tr>\n");
                    }
                    o.Append("</table>\n");
                }
                else if (x.Name == OdText + "section" || x.Name == OdText + "soft-page-break")
                {
                    Blocks(x, o);
                }
            }
        }

        var text = content.Descendants(OdOffice + "text").FirstOrDefault();
        if (text is not null) Blocks(text, sb);
        return new HtmlDoc(title ?? OutputPaths.StemOf(path), sb.ToString());
    }

    /// <summary>Escapes only markup characters; accents stay readable (output is UTF-8).</summary>
    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static HtmlDoc ReadText(string path)
    {
        var text = ReadTextFile(path).Replace("\r\n", "\n");
        var sb = new StringBuilder();
        foreach (var para in Regex.Split(text, @"\n\s*\n"))
        {
            if (para.Trim().Length == 0) continue;
            sb.Append("<p>").Append(string.Join("<br>", para.Split('\n').Select(Esc))).AppendLine("</p>");
        }
        return new HtmlDoc(OutputPaths.StemOf(path), sb.ToString());
    }

    private static readonly MarkdownPipeline MdPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static HtmlDoc ReadMarkdown(string path)
    {
        var text = ReadTextFile(path);
        var html = Markdown.ToHtml(text, MdPipeline);
        var title = Regex.Match(text, @"^#\s+(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : OutputPaths.StemOf(path);
        return new HtmlDoc(title, InlineLocalImages(html, Path.GetDirectoryName(Path.GetFullPath(path))!));
    }

    public static HtmlDoc ReadHtml(string path)
    {
        var doc = Parser.ParseDocument(ReadTextFile(path));
        foreach (var s in doc.QuerySelectorAll("script, noscript").ToList()) s.Remove();
        var title = doc.Title is { Length: > 0 } t ? t : doc.QuerySelector("h1")?.TextContent.Trim() ?? OutputPaths.StemOf(path);
        var body = doc.Body?.InnerHtml ?? "";
        return new HtmlDoc(title, InlineLocalImages(body, Path.GetDirectoryName(Path.GetFullPath(path))!));
    }

    /// <summary>Turns relative image paths into data URIs so the HTML is self-contained.</summary>
    private static string InlineLocalImages(string html, string baseDir)
    {
        var doc = Parser.ParseDocument("<body>" + html + "</body>");
        var changed = false;
        foreach (var img in doc.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src")!;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || src.Contains("://")) continue;
            try
            {
                var file = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(src).Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(file) || new FileInfo(file).Length > 25_000_000) continue;
                img.SetAttribute("src", DataUri(File.ReadAllBytes(file), MimeOf(file)));
                changed = true;
            }
            catch
            {
                // leave the reference as it is
            }
        }
        return changed ? doc.Body!.InnerHtml : html;
    }

    private static string MimeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

    private static string DataUri(byte[] data, string mime) => $"data:{mime};base64,{Convert.ToBase64String(data)}";

    // ---------- DOCX ----------

    public static HtmlDoc ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException(T("DOCX sem conteúdo."));
        var reader = new DocxHtml(main);
        var body = reader.Render();
        var title = doc.PackageProperties.Title is { Length: > 0 } t ? t : reader.FirstHeading ?? OutputPaths.StemOf(path);
        return new HtmlDoc(title, body);
    }

    private sealed class DocxHtml
    {
        private readonly MainDocumentPart _main;
        private readonly Dictionary<string, string> _styleNames = new();
        private readonly StringBuilder _sb = new();
        private readonly List<string> _openLists = [];

        public string? FirstHeading { get; private set; }

        public DocxHtml(MainDocumentPart main)
        {
            _main = main;
            foreach (var s in main.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? [])
            {
                if (s.StyleId?.Value is { } id) _styleNames[id] = (s.StyleName?.Val?.Value ?? id).ToLowerInvariant();
            }
        }

        public string Render()
        {
            var body = _main.Document?.Body;
            if (body is not null) Blocks(body.ChildElements);
            CloseLists(0);
            return _sb.ToString();
        }

        private void Blocks(IEnumerable<OpenXmlElement> elements)
        {
            foreach (var e in elements)
            {
                switch (e)
                {
                    case W.Paragraph p:
                        Paragraph(p);
                        break;
                    case W.Table t:
                        CloseLists(0);
                        Table(t);
                        break;
                    case W.SdtBlock sdt:
                        Blocks(sdt.SdtContentBlock?.ChildElements ?? (IEnumerable<OpenXmlElement>)[]);
                        break;
                    case W.CustomXmlBlock cx:
                        Blocks(cx.ChildElements);
                        break;
                }
            }
        }

        private int HeadingLevel(W.Paragraph p)
        {
            var pPr = p.ParagraphProperties;
            var styleId = pPr?.ParagraphStyleId?.Val?.Value;
            if (styleId is not null && _styleNames.TryGetValue(styleId, out var name))
            {
                if (name == "title") return 1;
                if (name == "subtitle") return 2;
                var m = Regex.Match(name, @"^heading\s*(\d)$");
                if (m.Success) return Math.Clamp(int.Parse(m.Groups[1].Value), 1, 6);
            }
            if (pPr?.OutlineLevel?.Val?.Value is int lvl && lvl < 9) return Math.Clamp(lvl + 1, 1, 6);
            return 0;
        }

        private (string Tag, int Level)? ListInfo(W.Paragraph p)
        {
            var np = p.ParagraphProperties?.NumberingProperties;
            var numId = np?.NumberingId?.Val?.Value;
            if (numId is null or 0) return null;
            var level = np!.NumberingLevelReference?.Val?.Value ?? 0;
            var numbering = _main.NumberingDefinitionsPart?.Numbering;
            var inst = numbering?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var absId = inst?.AbstractNumId?.Val?.Value;
            var abs = numbering?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
            var fmt = abs?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level)?.NumberingFormat?.Val;
            var bullet = fmt is null || fmt.Value == W.NumberFormatValues.Bullet;
            return (bullet ? "ul" : "ol", level);
        }

        private void CloseLists(int keep)
        {
            while (_openLists.Count > keep)
            {
                _sb.Append("</li></").Append(_openLists[^1]).Append('>');
                _openLists.RemoveAt(_openLists.Count - 1);
            }
        }

        private void Paragraph(W.Paragraph p)
        {
            var content = Inlines(p).Trim();
            var list = ListInfo(p);
            if (list is { } li)
            {
                var depth = li.Level + 1;
                if (_openLists.Count > depth) CloseLists(depth);
                if (_openLists.Count == depth && _openLists[^1] != li.Tag) CloseLists(depth - 1);
                if (_openLists.Count == depth)
                {
                    _sb.Append("</li><li>");
                }
                while (_openLists.Count < depth)
                {
                    _sb.Append('<').Append(li.Tag).Append("><li>");
                    _openLists.Add(li.Tag);
                }
                _sb.Append(content);
                return;
            }

            CloseLists(0);
            if (content.Length == 0) return;
            var h = HeadingLevel(p);
            var align = p.ParagraphProperties?.Justification?.Val;
            var style = align is null ? "" :
                align.Value == W.JustificationValues.Center ? " style=\"text-align:center\"" :
                align.Value == W.JustificationValues.Right ? " style=\"text-align:right\"" : "";
            if (h > 0)
            {
                FirstHeading ??= WebUtility.HtmlDecode(Regex.Replace(content, "<[^>]+>", ""));
                _sb.Append($"<h{h}{style}>{content}</h{h}>\n");
            }
            else
            {
                _sb.Append($"<p{style}>{content}</p>\n");
            }
        }

        private void Table(W.Table t)
        {
            _sb.Append("<table>\n");
            var first = true;
            foreach (var row in t.Elements<W.TableRow>())
            {
                var header = first && row.TableRowProperties?.GetFirstChild<W.TableHeader>() is not null;
                _sb.Append("<tr>");
                foreach (var cell in row.Elements<W.TableCell>())
                {
                    var span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                    var tag = header ? "th" : "td";
                    _sb.Append('<').Append(tag);
                    if (span > 1) _sb.Append($" colspan=\"{span}\"");
                    _sb.Append('>');
                    var inner = new DocxHtml(_main);
                    inner.Blocks(cell.ChildElements);
                    inner.CloseLists(0);
                    _sb.Append(inner._sb.ToString().Trim());
                    _sb.Append("</").Append(tag).Append('>');
                }
                _sb.Append("</tr>\n");
                first = false;
            }
            _sb.Append("</table>\n");
        }

        private string Inlines(OpenXmlElement container)
        {
            var sb = new StringBuilder();
            foreach (var e in container.ChildElements)
            {
                switch (e)
                {
                    case W.Run r:
                        sb.Append(Run(r));
                        break;
                    case W.Hyperlink h:
                        var href = h.Id?.Value is { } id
                            ? _main.HyperlinkRelationships.FirstOrDefault(rel => rel.Id == id)?.Uri.ToString()
                            : h.Anchor?.Value is { } anchor ? "#" + anchor : null;
                        var inner = Inlines(h);
                        sb.Append(href is null ? inner : $"<a href=\"{Esc(href)}\">{inner}</a>");
                        break;
                    case W.DeletedRun:
                    case W.ParagraphProperties:
                        break;
                    case W.SdtRun or W.SimpleField or W.InsertedRun or W.CustomXmlRun or W.MoveToRun:
                        sb.Append(Inlines(e is W.SdtRun sr ? (OpenXmlElement?)sr.SdtContentRun ?? e : e));
                        break;
                }
            }
            return sb.ToString();
        }

        private string Run(W.Run r)
        {
            var sb = new StringBuilder();
            foreach (var c in r.ChildElements)
            {
                switch (c)
                {
                    case W.Text t: sb.Append(Esc(t.Text)); break;
                    case W.TabChar: sb.Append("&emsp;"); break;
                    case W.Break or W.CarriageReturn: sb.Append("<br>"); break;
                    case W.NoBreakHyphen: sb.Append('‑'); break;
                    case W.Drawing d: sb.Append(Image(d)); break;
                    case W.Picture pic: sb.Append(Image(pic)); break;
                }
            }
            var text = sb.ToString();
            if (text.Length == 0) return "";
            var rp = r.RunProperties;
            if (rp is null) return text;
            static bool On(W.OnOffType? v) => v is not null && (v.Val is null || v.Val.Value);
            if (rp.VerticalTextAlignment?.Val is { } va)
            {
                if (va.Value == W.VerticalPositionValues.Superscript) text = $"<sup>{text}</sup>";
                else if (va.Value == W.VerticalPositionValues.Subscript) text = $"<sub>{text}</sub>";
            }
            if (On(rp.Strike)) text = $"<s>{text}</s>";
            if (rp.Underline?.Val is { } u && u.Value != W.UnderlineValues.None) text = $"<u>{text}</u>";
            if (On(rp.Italic)) text = $"<em>{text}</em>";
            if (On(rp.Bold)) text = $"<strong>{text}</strong>";
            return text;
        }

        private string Image(OpenXmlElement drawing)
        {
            var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
            var rid = blip?.Embed?.Value;
            if (rid is null)
            {
                // Old VML pictures keep the id on v:imagedata.
                rid = drawing.Descendants().FirstOrDefault(x => x.LocalName == "imagedata")?
                    .GetAttributes().FirstOrDefault(a => a.LocalName == "id").Value;
            }
            if (rid is null || _main.GetPartById(rid) is not ImagePart part) return "";
            using var s = part.GetStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var width = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault()?.Cx?.Value;
            var w = width is > 0 ? $" width=\"{(int)(width.Value / 9525)}\"" : "";
            return $"<img src=\"{DataUri(ms.ToArray(), part.ContentType)}\"{w} alt=\"\">";
        }
    }

    // ---------- EPUB ----------

    public static HtmlDoc ReadEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace cn = "urn:oasis:names:tc:opendocument:xmlns:container";
        var container = XDocument.Load(Open(zip, "META-INF/container.xml"));
        var opfPath = container.Descendants(cn + "rootfile").First().Attribute("full-path")!.Value;
        var opf = XDocument.Load(Open(zip, opfPath));
        XNamespace opfNs = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var opfDir = Path.GetDirectoryName(opfPath.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
        var manifest = opf.Descendants(opfNs + "item").ToDictionary(i => i.Attribute("id")!.Value, i => i.Attribute("href")!.Value);
        var title = opf.Descendants(dc + "title").FirstOrDefault()?.Value ?? OutputPaths.StemOf(path);

        var sb = new StringBuilder();
        foreach (var itemref in opf.Descendants(opfNs + "itemref"))
        {
            if (!manifest.TryGetValue(itemref.Attribute("idref")!.Value, out var href)) continue;
            var itemPath = Combine(opfDir, Uri.UnescapeDataString(href.Split('#')[0]));
            var entry = zip.GetEntry(itemPath);
            if (entry is null) continue;
            using var reader = new StreamReader(entry.Open());
            var page = Parser.ParseDocument(reader.ReadToEnd());
            foreach (var s in page.QuerySelectorAll("script").ToList()) s.Remove();
            var itemDir = itemPath.Contains('/') ? itemPath[..itemPath.LastIndexOf('/')] : "";
            foreach (var img in page.QuerySelectorAll("img[src], image"))
            {
                var attr = img.HasAttribute("src") ? "src" : img.Attributes.FirstOrDefault(a => a.LocalName == "href")?.Name;
                if (attr is null) continue;
                var src = img.GetAttribute(attr)!;
                var imgEntry = zip.GetEntry(Combine(itemDir, Uri.UnescapeDataString(src)));
                if (imgEntry is null) continue;
                using var ims = imgEntry.Open();
                using var ms = new MemoryStream();
                ims.CopyTo(ms);
                img.SetAttribute(attr, DataUri(ms.ToArray(), MimeOf(imgEntry.Name)));
            }
            if (sb.Length > 0) sb.Append("<div style=\"page-break-before:always\"></div>\n");
            sb.Append(page.Body?.InnerHtml ?? "");
        }
        return new HtmlDoc(title, sb.ToString());
    }

    private static Stream Open(ZipArchive zip, string name) =>
        (zip.GetEntry(name) ?? throw new InvalidDataException(T("EPUB inválido: falta {0}", name))).Open();

    /// <summary>Joins zip paths, resolving "../".</summary>
    private static string Combine(string dir, string rel)
    {
        var parts = new List<string>();
        foreach (var p in (dir.Length > 0 ? dir + "/" + rel : rel).Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }

    // ====================================================================== writing

    private const string Css = """
        body { font-family: "Segoe UI", system-ui, sans-serif; font-size: 11pt; line-height: 1.55; color: #1b1b1b; max-width: 46rem; margin: 2rem auto; padding: 0 1rem; }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.4em 0 .5em; }
        img, svg { max-width: 100%; height: auto; }
        table { border-collapse: collapse; margin: 1em 0; }
        th, td { border: 1px solid #c8c8c8; padding: 4px 8px; vertical-align: top; }
        th p, td p { margin: 0; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: .92em; }
        pre { background: #f4f4f4; padding: .8em; overflow-x: auto; white-space: pre-wrap; }
        blockquote { border-left: 3px solid #ccc; margin-left: 0; padding-left: 1em; color: #555; }
        @media print { body { max-width: none; margin: 0; padding: 0; } }
        """;

    public static string Standalone(HtmlDoc d) =>
        $"<!doctype html>\n<html><head><meta charset=\"utf-8\">\n<title>{Esc(d.Title)}</title>\n<style>{Css}</style>\n</head><body>\n{d.Body}\n</body></html>\n";

    public static async Task WriteAsync(HtmlDoc d, string target, string outPath, CancellationToken ct = default)
    {
        switch (Formats.Canonical(target))
        {
            case "html":
                await File.WriteAllTextAsync(outPath, Standalone(d), new UTF8Encoding(false), ct).ConfigureAwait(false);
                break;
            case "txt":
                await File.WriteAllTextAsync(outPath, ToPlainText(d.Body), new UTF8Encoding(false), ct).ConfigureAwait(false);
                break;
            case "md":
                await File.WriteAllTextAsync(outPath, ToMarkdown(d, outPath), new UTF8Encoding(false), ct).ConfigureAwait(false);
                break;
            case "docx":
                await WriteDocxAsync(d, outPath, ct).ConfigureAwait(false);
                break;
            case "epub":
                WriteEpub(d, outPath);
                break;
            case "pdf":
                await ToPdfAsync(d, outPath, ct).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(target);
        }
    }

    /// <summary>Reads any supported input and writes it in <paramref name="target"/> next to it.</summary>
    public static async Task<string> ConvertAsync(string input, string target, CancellationToken ct = default)
    {
        var d = Read(input);
        var outPath = OutputPaths.For(input, Formats.Canonical(target));
        await WriteAsync(d, target, outPath, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task ToPdfAsync(HtmlDoc d, string pdfPath, CancellationToken ct = default)
    {
        var printer = HtmlFileToPdf ?? throw new NotSupportedException(T("Gerar PDF precisa do WebView2 (motor do Edge), disponível quando o WinKame está aberto."));
        var dir = OutputPaths.NewTempDir();
        try
        {
            var html = Path.Combine(dir, "doc.html");
            await File.WriteAllTextAsync(html, Standalone(d), new UTF8Encoding(false), ct).ConfigureAwait(false);
            await printer(html, pdfPath, ct).ConfigureAwait(false);
        }
        finally
        {
            OutputPaths.TryDelete(dir);
        }
    }

    // ---------- plain text ----------

    private static readonly HashSet<string> BlockTags =
        ["P", "DIV", "H1", "H2", "H3", "H4", "H5", "H6", "LI", "TR", "SECTION", "ARTICLE", "BLOCKQUOTE", "PRE", "TABLE", "UL", "OL", "HR", "HEADER", "FOOTER", "FIGURE"];

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    public static string ToPlainText(string bodyHtml)
    {
        var doc = Parser.ParseDocument("<body>" + bodyHtml + "</body>");
        var sb = new StringBuilder();

        // Paragraphs are separated by a blank line; list items and table rows by a single line break.
        void Blank()
        {
            var trimmed = sb.ToString().TrimEnd(' ');
            sb.Clear().Append(trimmed);
            if (sb.Length == 0) return;
            if (!trimmed.EndsWith("\n\n")) sb.Append(trimmed.EndsWith('\n') ? "\n" : "\n\n");
        }

        void Line()
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        }

        void List(IElement list, int depth)
        {
            if (depth == 0) Blank(); else Line();
            var n = 0;
            foreach (var li in list.Children.Where(c => c.TagName == "LI"))
            {
                Line();
                sb.Append(new string(' ', depth * 2)).Append(list.TagName == "OL" ? $"{++n}. " : "- ");
                foreach (var c in li.ChildNodes)
                {
                    if (c is IElement { TagName: "UL" or "OL" } sub) List(sub, depth + 1);
                    else if (c is IElement { TagName: "P" } p) sb.Append(Collapse(p.TextContent)).Append(' ');
                    else Inline(c);
                }
            }
            if (depth == 0) Blank();
        }

        void Table(IElement table)
        {
            Blank();
            foreach (var row in table.QuerySelectorAll("tr"))
            {
                Line();
                sb.Append(string.Join('\t', row.Children.Where(c => c.TagName is "TD" or "TH").Select(c => Collapse(c.TextContent))));
            }
            Blank();
        }

        void Inline(INode c)
        {
            if (c is IText t) sb.Append(Regex.Replace(t.Data, @"\s+", " "));
            else if (c is IElement e) Element(e);
        }

        void Element(IElement e)
        {
            switch (e.TagName)
            {
                case "BR": sb.Append('\n'); return;
                case "SCRIPT" or "STYLE": return;
                case "UL" or "OL": List(e, 0); return;
                case "TABLE": Table(e); return;
                case "PRE": Blank(); sb.Append(e.TextContent.TrimEnd()); Blank(); return;
            }
            var block = BlockTags.Contains(e.TagName);
            if (block) Blank();
            foreach (var c in e.ChildNodes) Inline(c);
            if (block) Blank();
        }

        foreach (var c in doc.Body!.ChildNodes) Inline(c);
        var text = Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim() + Environment.NewLine;
    }

    // ---------- markdown ----------

    private static string ToMarkdown(HtmlDoc d, string outPath)
    {
        // Embedded images become files in a folder next to the .md.
        var doc = Parser.ParseDocument("<body>" + d.Body + "</body>");
        var media = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileNameWithoutExtension(outPath) + " - " + T("mídia"));
        var n = 0;
        foreach (var img in doc.QuerySelectorAll("img[src^='data:']"))
        {
            var (bytes, ext) = DecodeDataUri(img.GetAttribute("src")!);
            if (bytes is null) continue;
            Directory.CreateDirectory(media);
            var name = $"imagem{++n:000}.{ext}";
            File.WriteAllBytes(Path.Combine(media, name), bytes);
            img.SetAttribute("src", Uri.EscapeDataString(Path.GetFileName(media)) + "/" + name);
        }
        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            Formatting = { RemoveComments = true },
            Links = { SmartHref = true },
            Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
        });
        var md = converter.Convert(doc.Body!.InnerHtml).Trim();
        if (!md.StartsWith("# ", StringComparison.Ordinal) && d.Title.Length > 0 && !d.Body.Contains("<h1", StringComparison.OrdinalIgnoreCase))
        {
            md = $"# {d.Title}\n\n{md}";
        }
        return md + Environment.NewLine;
    }

    [GeneratedRegex(@"^data:(?<mime>[\w/+.-]+)?(?<b64>;base64)?,(?<data>.*)$", RegexOptions.Singleline)]
    private static partial Regex DataUriRx();

    private static (byte[]? Bytes, string Ext) DecodeDataUri(string uri)
    {
        var m = DataUriRx().Match(uri);
        if (!m.Success || !m.Groups["b64"].Success) return (null, "");
        var ext = m.Groups["mime"].Value switch
        {
            "image/png" => "png",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            "image/webp" => "webp",
            "image/bmp" => "bmp",
            _ => "jpg",
        };
        try { return (Convert.FromBase64String(m.Groups["data"].Value), ext); }
        catch (FormatException) { return (null, ""); }
    }

    // ---------- docx ----------

    private static async Task WriteDocxAsync(HtmlDoc d, string outPath, CancellationToken ct)
    {
        using var pkg = WordprocessingDocument.Create(outPath, WordprocessingDocumentType.Document);
        var main = pkg.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        var converter = new HtmlConverter(main);
        await converter.ParseBody(d.Body, ct).ConfigureAwait(false);
        main.Document.Save();
        pkg.PackageProperties.Title = d.Title;
    }

    // ---------- epub ----------

    public static void WriteEpub(HtmlDoc d, string outPath)
    {
        var doc = Parser.ParseDocument("<body>" + d.Body + "</body>");
        var body = doc.Body!;

        // Images move out of data URIs into the package.
        var images = new List<(string Name, byte[] Data, string Mime)>();
        foreach (var img in body.QuerySelectorAll("img[src^='data:']"))
        {
            var (bytes, ext) = DecodeDataUri(img.GetAttribute("src")!);
            if (bytes is null) { img.Remove(); continue; }
            var name = $"img{images.Count + 1:000}.{ext}";
            images.Add((name, bytes, MimeOf(name)));
            img.SetAttribute("src", "../images/" + name);
            if (!img.HasAttribute("alt")) img.SetAttribute("alt", "");
        }

        // One chapter per top-level <h1>.
        var chapters = new List<(string Title, List<INode> Nodes)>();
        foreach (var node in body.ChildNodes.ToList())
        {
            var isH1 = node is IElement { TagName: "H1" };
            if (chapters.Count == 0 || (isH1 && chapters[^1].Nodes.Any(n => n is IElement)))
            {
                chapters.Add((isH1 ? node.TextContent.Trim() : d.Title, []));
            }
            chapters[^1].Nodes.Add(node);
        }
        if (chapters.Count == 0) chapters.Add((d.Title, []));

        var uid = "urn:uuid:" + Guid.NewGuid();
        var lang = "pt";
        if (File.Exists(outPath)) File.Delete(outPath);
        using var fs = File.Create(outPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        void Add(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression); // must be first and stored
        Add("META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        Add("OEBPS/style.css", Css.Replace("max-width: 46rem; margin: 2rem auto; padding: 0 1rem;", "margin: 0;"));

        var x = XhtmlMarkupFormatter.Instance;
        for (var i = 0; i < chapters.Count; i++)
        {
            var inner = string.Concat(chapters[i].Nodes.Select(n => n.ToHtml(x)));
            Add($"OEBPS/text/ch{i + 1:000}.xhtml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{lang}" xml:lang="{lang}">
                <head><meta charset="utf-8"/><title>{Esc(chapters[i].Title)}</title><link rel="stylesheet" type="text/css" href="../style.css"/></head>
                <body>{inner}</body>
                </html>
                """);
        }
        foreach (var (name, data, _) in images)
        {
            using var s = zip.CreateEntry("OEBPS/images/" + name, CompressionLevel.NoCompression).Open();
            s.Write(data);
        }

        var toc = string.Join("\n", chapters.Select((c, i) => $"<li><a href=\"text/ch{i + 1:000}.xhtml\">{Esc(c.Title)}</a></li>"));
        Add("OEBPS/nav.xhtml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{lang}" xml:lang="{lang}">
            <head><meta charset="utf-8"/><title>{Esc(d.Title)}</title></head>
            <body><nav epub:type="toc" id="toc"><h1>{Esc(d.Title)}</h1><ol>
            {toc}
            </ol></nav></body>
            </html>
            """);

        var manifest = new StringBuilder();
        manifest.AppendLine("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        manifest.AppendLine("<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>");
        for (var i = 0; i < chapters.Count; i++) manifest.AppendLine($"<item id=\"ch{i + 1}\" href=\"text/ch{i + 1:000}.xhtml\" media-type=\"application/xhtml+xml\"/>");
        for (var i = 0; i < images.Count; i++) manifest.AppendLine($"<item id=\"img{i + 1}\" href=\"images/{images[i].Name}\" media-type=\"{images[i].Mime}\"/>");
        var spine = string.Join("\n", chapters.Select((_, i) => $"<itemref idref=\"ch{i + 1}\"/>"));
        Add("OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid" xml:lang="{lang}">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="uid">{uid}</dc:identifier>
                <dc:title>{Esc(d.Title)}</dc:title>
                <dc:language>{lang}</dc:language>
                <meta property="dcterms:modified">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>
              </metadata>
              <manifest>
            {manifest}  </manifest>
              <spine>
            {spine}
              </spine>
            </package>
            """);
    }
}
