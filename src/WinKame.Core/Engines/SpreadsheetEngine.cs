using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace WinKame.Core.Engines;

/// <summary>
/// CSV, TSV and XLSX in-process. PDF via Excel when installed, otherwise a native HTML table printed by WebView2.
/// ODS, XLS and Numbers need Excel or LibreOffice.
/// </summary>
public static class SpreadsheetEngine
{
    private static bool NeedsOffice(string ext) => ext is "ods" or "xls" or "numbers";

    public static ExternalTool? RequiredTool(string input, string target)
    {
        target = Formats.Canonical(target);
        var src = Formats.Canonical(Formats.Ext(input));
        if (src == "numbers") return ExternalTool.LibreOffice;
        if ((NeedsOffice(src) || target == "ods") && !OfficeEngine.IsInstalled(OfficeApp.Excel)) return ExternalTool.LibreOffice;
        return null;
    }

    public static async Task<IReadOnlyList<string>> ConvertAsync(string input, string target, CancellationToken ct)
    {
        target = Formats.Canonical(target);
        var src = Formats.Canonical(Formats.Ext(input));
        var temps = new List<string>();
        try
        {
            // Excel keeps formatting, formulas and page setup.
            if (target is "pdf" or "ods" or "xlsx" && src != "numbers"
                && await OfficeEngine.TryConvertAsync(input, target, ct).ConfigureAwait(false) is { } byExcel)
            {
                return [byExcel];
            }

            // Bring legacy formats to XLSX first.
            var working = input;
            if (NeedsOffice(src))
            {
                var dir = OutputPaths.NewTempDir();
                temps.Add(dir);
                var viaExcel = src != "numbers"
                    ? await OfficeEngine.TryConvertAsync(input, "xlsx", ct, Path.Combine(dir, "book.xlsx")).ConfigureAwait(false)
                    : null;
                if (viaExcel is not null)
                {
                    working = viaExcel;
                }
                else
                {
                    var produced = await DocumentEngine.LibreOfficeAsync(input, "xlsx", ct).ConfigureAwait(false);
                    temps.Add(Path.GetDirectoryName(produced)!);
                    working = produced;
                }
                if (target == "xlsx") return [OutputPaths.MoveInto(working, OutputPaths.For(input, "xlsx"))];
            }

            if (target == "ods")
            {
                var source = working;
                if (Formats.Canonical(Formats.Ext(working)) is "csv" or "tsv")
                {
                    // LibreOffice guesses CSV options poorly; hand it a clean XLSX.
                    var dir = OutputPaths.NewTempDir();
                    temps.Add(dir);
                    source = Path.Combine(dir, OutputPaths.StemOf(input) + ".xlsx");
                    WriteXlsx(ReadTable(working), source);
                }
                var produced = await DocumentEngine.LibreOfficeAsync(source, "ods", ct).ConfigureAwait(false);
                temps.Add(Path.GetDirectoryName(produced)!);
                return [OutputPaths.MoveInto(produced, OutputPaths.For(input, "ods"))];
            }

            var outPath = OutputPaths.For(input, target);
            switch (target)
            {
                case "xlsx": WriteXlsx(ReadTable(working), outPath); break;
                case "csv": WriteDelimited(ReadTable(working), outPath, ','); break;
                case "tsv": WriteDelimited(ReadTable(working), outPath, '\t'); break;
                case "pdf": await NativeDocs.ToPdfAsync(ToHtmlDoc(working, OutputPaths.StemOf(input)), outPath, ct).ConfigureAwait(false); break;
                default: throw new NotSupportedException(target);
            }
            return [outPath];
        }
        finally
        {
            foreach (var t in temps) OutputPaths.TryDelete(t);
        }
    }

    /// <summary>Every sheet as an HTML table (CSV/TSV have one), for printing to PDF.</summary>
    public static HtmlDoc ToHtmlDoc(string path, string title)
    {
        var sb = new StringBuilder("<style>table{font-size:9pt}td,th{white-space:nowrap}</style>\n");
        void Table(string? name, IEnumerable<string[]> rows)
        {
            if (name is not null) sb.Append("<h2>").Append(System.Net.WebUtility.HtmlEncode(name)).Append("</h2>\n");
            sb.Append("<table>\n");
            var first = true;
            foreach (var row in rows)
            {
                var tag = first ? "th" : "td";
                sb.Append("<tr>");
                foreach (var cell in row) sb.Append('<').Append(tag).Append('>').Append(System.Net.WebUtility.HtmlEncode(cell ?? "")).Append("</").Append(tag).Append('>');
                sb.Append("</tr>\n");
                first = false;
            }
            sb.Append("</table>\n");
        }

        if (Formats.Canonical(Formats.Ext(path)) == "xlsx")
        {
            using var wb = new XLWorkbook(path);
            var many = wb.Worksheets.Count > 1;
            foreach (var ws in wb.Worksheets) Table(many ? ws.Name : null, SheetRows(ws));
        }
        else
        {
            Table(null, ReadTable(path));
        }
        return new HtmlDoc(title, sb.ToString());
    }

    private static List<string[]> SheetRows(IXLWorksheet ws)
    {
        var used = ws.RangeUsed();
        if (used is null) return [];
        var firstCol = used.FirstColumn().ColumnNumber();
        var lastCol = used.LastColumn().ColumnNumber();
        var rows = new List<string[]>();
        foreach (var row in used.Rows())
        {
            var cells = new string[lastCol - firstCol + 1];
            for (var c = firstCol; c <= lastCol; c++) cells[c - firstCol] = ws.Cell(row.RowNumber(), c).GetFormattedString();
            rows.Add(cells);
        }
        return rows;
    }

    public static List<string[]> ReadTable(string path)
    {
        var ext = Formats.Canonical(Formats.Ext(path));
        if (ext == "xlsx")
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();
            var used = ws.RangeUsed();
            if (used is null) return [];
            var rows = new List<string[]>();
            var firstCol = used.FirstColumn().ColumnNumber();
            var lastCol = used.LastColumn().ColumnNumber();
            foreach (var row in used.Rows())
            {
                var cells = new string[lastCol - firstCol + 1];
                for (var c = firstCol; c <= lastCol; c++) cells[c - firstCol] = row.Worksheet.Cell(row.RowNumber(), c).GetFormattedString();
                rows.Add(cells);
            }
            return rows;
        }

        var text = ReadTextSmart(path);
        var sep = ext == "tsv" ? '\t' : GuessDelimiter(text);
        return ParseDelimited(text, sep);
    }

    private static string ReadTextSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static char GuessDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var counts = new[] { ',', ';', '\t', '|' }.Select(c => (c, n: firstLine.Count(x => x == c))).OrderByDescending(x => x.n).First();
        return counts.n > 0 ? counts.c : ',';
    }

    public static List<string[]> ParseDelimited(string text, char sep)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && field.Length == 0) inQuotes = true;
            else if (ch == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }

    public static void WriteDelimited(IEnumerable<string[]> rows, string path, char sep)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(sep, row.Select(c => Quote(c ?? "", sep)));
            sb.Append("\r\n");
        }
        // BOM so Excel opens accented text correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Quote(string value, char sep) =>
        value.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static void WriteXlsx(IReadOnlyList<string[]> rows, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Planilha1");
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                var v = rows[r][c] ?? "";
                var cell = ws.Cell(r + 1, c + 1);
                // Keep codes like "007" as text; only plain numbers become numbers.
                var leadingZero = v.Length > 1 && v[0] == '0' && v[1] != '.';
                if (v.Length is > 0 and < 16 && !leadingZero
                    && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    cell.Value = d;
                    continue;
                }
                cell.Value = v;
            }
        }
        if (rows.Count > 0) ws.Columns().AdjustToContents(1, Math.Min(rows.Count, 200));
        wb.SaveAs(path);
    }
}
