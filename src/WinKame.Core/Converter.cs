using WinKame.Core.Engines;

namespace WinKame.Core;

/// <summary>Single entry point for "convert this file to that format".</summary>
public static class Converter
{
    public static async Task<IReadOnlyList<string>> ConvertAsync(string input, string target, IProgress<double>? progress, CancellationToken ct)
    {
        target = Formats.Canonical(target);
        var own = Formats.Canonical(Formats.Ext(input));
        if (own == target) return [];

        var kind = Formats.KindOf(input);
        switch (kind)
        {
            case FileKind.Image:
                if (target is "mp4" or "webm")
                {
                    return [await MediaEngine.ConvertAsync(input, target, progress, ct).ConfigureAwait(false)];
                }
                return [await Task.Run(() => ImageEngine.Convert(input, target), ct).ConfigureAwait(false)];

            case FileKind.Video:
            case FileKind.Audio:
                return [await MediaEngine.ConvertAsync(input, target, progress, ct).ConfigureAwait(false)];

            case FileKind.Pdf:
                return await ConvertPdfAsync(input, target, progress, ct).ConfigureAwait(false);

            case FileKind.Document:
                return await DocumentEngine.ConvertDocumentAsync(input, target, progress, ct).ConfigureAwait(false);

            case FileKind.Epub:
                return await DocumentEngine.ConvertDocumentAsync(input, target, progress, ct).ConfigureAwait(false);

            case FileKind.Slides:
                return await DocumentEngine.ConvertSlidesAsync(input, target, progress, ct).ConfigureAwait(false);

            case FileKind.Spreadsheet:
                return await SpreadsheetEngine.ConvertAsync(input, target, ct).ConfigureAwait(false);

            case FileKind.Subtitle:
                return [await Task.Run(() => SubtitleEngine.Convert(input, target), ct).ConfigureAwait(false)];

            case FileKind.Archive:
                return [await Task.Run(() => ArchiveEngine.Convert(input, target, progress, ct), ct).ConfigureAwait(false)];

            default:
                throw new NotSupportedException(T("Não sei converter {0}.", Path.GetFileName(input)));
        }
    }

    private static async Task<IReadOnlyList<string>> ConvertPdfAsync(string pdf, string target, IProgress<double>? progress, CancellationToken ct)
    {
        switch (target)
        {
            case "png":
            case "jpg":
                return await PdfEngine.RenderPagesAsync(pdf, target, 300, progress, ct).ConfigureAwait(false);
            case "txt":
            {
                var outPath = OutputPaths.For(pdf, "txt");
                await File.WriteAllTextAsync(outPath, await Task.Run(() => PdfEngine.ExtractText(pdf), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                return [outPath];
            }
            case "md":
            {
                var outPath = OutputPaths.For(pdf, "md");
                await File.WriteAllTextAsync(outPath, await Task.Run(() => PdfEngine.ToMarkdown(pdf), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                return [outPath];
            }
            case "html":
            {
                var outPath = OutputPaths.For(pdf, "html");
                await File.WriteAllTextAsync(outPath, await Task.Run(() => PdfEngine.ToHtml(pdf), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                return [outPath];
            }
            case "xlsx":
            {
                var outPath = OutputPaths.For(pdf, "xlsx");
                var rows = await Task.Run(() => PdfEngine.ToTable(pdf), ct).ConfigureAwait(false);
                SpreadsheetEngine.WriteXlsx(rows, outPath);
                return [outPath];
            }
            default:
                return [await DocumentEngine.PdfToAsync(pdf, target, ct).ConfigureAwait(false)];
        }
    }

    /// <summary>Which external tool a conversion will need, if any, so the UI can warn before starting.</summary>
    public static ExternalTool? RequiredTool(string input, string target)
    {
        target = Formats.Canonical(target);
        return Formats.KindOf(input) switch
        {
            FileKind.Video or FileKind.Audio => ExternalTool.FFmpeg,
            FileKind.Image when target is "mp4" or "webm" => ExternalTool.FFmpeg,
            FileKind.Pdf or FileKind.Document or FileKind.Epub or FileKind.Slides => DocumentEngine.RequiredTool(input, target),
            FileKind.Spreadsheet => SpreadsheetEngine.RequiredTool(input, target),
            _ => null,
        };
    }
}
