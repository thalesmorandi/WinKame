namespace WinKame.Core.Engines;

/// <summary>
/// Word processing, slides and e-books. Order of preference:
/// Microsoft Office when installed (best fidelity) → native .NET + WebView2 → LibreOffice for legacy formats.
/// </summary>
public static class DocumentEngine
{
    private static readonly SemaphoreSlim LibreLock = new(1, 1);

    /// <summary>Formats only Office or LibreOffice can read (RTF and ODT are read natively).</summary>
    private static bool IsLegacy(string ext) => ext is "doc" or "pages";

    /// <summary>Runs LibreOffice headless; returns the produced file inside a temp folder.</summary>
    public static async Task<string> LibreOfficeAsync(string input, string convertTo, CancellationToken ct, string? infilter = null)
    {
        var exe = ToolLocator.Require(ExternalTool.LibreOffice);
        var outDir = OutputPaths.NewTempDir();
        // A private profile so a LibreOffice window the user has open doesn't block us.
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKame", "lo-profile");
        Directory.CreateDirectory(profile);
        var args = new List<string>
        {
            "-env:UserInstallation=" + new Uri(profile).AbsoluteUri,
            "--headless", "--norestore", "--nolockcheck", "--nodefault", "--nologo",
        };
        if (infilter is not null) args.Add($"--infilter={infilter}");
        args.AddRange(["--convert-to", convertTo, "--outdir", outDir, Path.GetFullPath(input)]);

        await LibreLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ProcessRunner.RunAsync(exe, args, ct).ConfigureAwait(false);
        }
        finally
        {
            LibreLock.Release();
        }
        return Directory.EnumerateFiles(outDir).FirstOrDefault()
            ?? throw new InvalidOperationException(T("O LibreOffice não gerou nenhum arquivo. O documento pode estar protegido ou corrompido."));
    }

    private static async Task<string> LibreInto(string input, string target, CancellationToken ct, string? infilter = null, string? convertTo = null)
    {
        var produced = await LibreOfficeAsync(input, convertTo ?? target, ct, infilter).ConfigureAwait(false);
        try { return OutputPaths.MoveInto(produced, OutputPaths.For(input, target)); }
        finally { OutputPaths.TryDelete(Path.GetDirectoryName(produced)); }
    }

    /// <summary>Converts a word-processing document or e-book to <paramref name="target"/>.</summary>
    public static async Task<IReadOnlyList<string>> ConvertDocumentAsync(string input, string target, IProgress<double>? progress, CancellationToken ct)
    {
        target = Formats.Canonical(target);
        var src = Formats.Canonical(Formats.Ext(input));

        // Office gives the most faithful PDF/DOCX/RTF/ODT for Word documents.
        if (target is "pdf" or "docx" or "rtf" or "odt"
            && await OfficeEngine.TryConvertAsync(input, target, ct).ConfigureAwait(false) is { } byOffice)
        {
            return [byOffice];
        }

        var temps = new List<string>();
        try
        {
            var working = input;
            if (IsLegacy(src) || target is "rtf" or "odt")
            {
                // Word reads the legacy file into DOCX; the rest is native.
                var dir = OutputPaths.NewTempDir();
                temps.Add(dir);
                var viaWord = IsLegacy(src)
                    ? await OfficeEngine.TryConvertAsync(input, "docx", ct, Path.Combine(dir, "doc.docx")).ConfigureAwait(false)
                    : null;
                if (viaWord is not null)
                {
                    working = viaWord;
                }
                else
                {
                    // Without (a working) Office these need LibreOffice.
                    if (target is "pdf" or "docx" or "rtf" or "odt") return [await LibreInto(input, target, ct).ConfigureAwait(false)];
                    var docx = await LibreOfficeAsync(input, "docx", ct).ConfigureAwait(false);
                    temps.Add(Path.GetDirectoryName(docx)!);
                    working = docx;
                }
            }

            if (target is "png" or "jpg")
            {
                // Word lays the pages out exactly; render those.
                var dir = OutputPaths.NewTempDir();
                temps.Add(dir);
                if (await OfficeEngine.TryConvertAsync(working, "pdf", ct, Path.Combine(dir, "doc.pdf")).ConfigureAwait(false) is { } pdfByWord)
                {
                    return await PdfEngine.RenderPagesAsync(pdfByWord, target, 150, null, ct, nameSource: input).ConfigureAwait(false);
                }
            }

            var doc = NativeDocs.Read(working);
            progress?.Report(0.4);
            switch (target)
            {
                case "pdf":
                {
                    var pdf = OutputPaths.For(input, "pdf");
                    await NativeDocs.ToPdfAsync(doc, pdf, ct).ConfigureAwait(false);
                    return [pdf];
                }
                case "png":
                case "jpg":
                {
                    var dir = OutputPaths.NewTempDir();
                    temps.Add(dir);
                    var pdf = Path.Combine(dir, "doc.pdf");
                    await NativeDocs.ToPdfAsync(doc, pdf, ct).ConfigureAwait(false);
                    progress?.Report(0.7);
                    return await PdfEngine.RenderPagesAsync(pdf, target, 150, null, ct, nameSource: input).ConfigureAwait(false);
                }
                default:
                {
                    var outPath = OutputPaths.For(input, target);
                    await NativeDocs.WriteAsync(doc, target, outPath, ct).ConfigureAwait(false);
                    return [outPath];
                }
            }
        }
        finally
        {
            foreach (var t in temps) OutputPaths.TryDelete(t);
        }
    }

    public static async Task<IReadOnlyList<string>> ConvertSlidesAsync(string input, string target, IProgress<double>? progress, CancellationToken ct)
    {
        target = Formats.Canonical(target);
        if (target is "pdf" or "pptx" or "odp")
        {
            if (await OfficeEngine.TryConvertAsync(input, target, ct).ConfigureAwait(false) is { } byOffice) return [byOffice];
            return [await LibreInto(input, target, ct).ConfigureAwait(false)];
        }

        // Images: go through PDF, then render each slide.
        string? cleanup = OutputPaths.NewTempDir();
        var pdf = await OfficeEngine.TryConvertAsync(input, "pdf", ct, Path.Combine(cleanup, "slides.pdf")).ConfigureAwait(false);
        if (pdf is null)
        {
            OutputPaths.TryDelete(cleanup);
            pdf = await LibreOfficeAsync(input, "pdf", ct).ConfigureAwait(false);
            cleanup = Path.GetDirectoryName(pdf);
        }
        try
        {
            progress?.Report(0.5);
            return await PdfEngine.RenderPagesAsync(pdf, target, 150, null, ct, nameSource: input).ConfigureAwait(false);
        }
        finally
        {
            OutputPaths.TryDelete(cleanup);
        }
    }

    /// <summary>PDF to editable formats: rebuilt natively from the PDF's text blocks.</summary>
    public static async Task<string> PdfToAsync(string pdf, string target, CancellationToken ct)
    {
        target = Formats.Canonical(target);
        if (target == "pptx")
        {
            return await LibreInto(pdf, "pptx", ct, "impress_pdf_import").ConfigureAwait(false);
        }
        var doc = await Task.Run(() => PdfEngine.ToHtmlDoc(pdf), ct).ConfigureAwait(false);
        var outPath = OutputPaths.For(pdf, target);
        await NativeDocs.WriteAsync(doc, target, outPath, ct).ConfigureAwait(false);
        return outPath;
    }

    /// <summary>Which external program a document conversion still needs, given what's installed.</summary>
    public static ExternalTool? RequiredTool(string input, string target)
    {
        target = Formats.Canonical(target);
        var src = Formats.Canonical(Formats.Ext(input));
        var kind = Formats.KindOf(input);
        if (kind == FileKind.Slides || (kind == FileKind.Pdf && target == "pptx"))
        {
            return target != "pptx" || kind != FileKind.Pdf
                ? (OfficeEngine.IsInstalled(OfficeApp.PowerPoint) && src != "key" ? null : ExternalTool.LibreOffice)
                : ExternalTool.LibreOffice;
        }
        if (kind is FileKind.Document or FileKind.Epub)
        {
            if (OfficeEngine.AppFor(input, target) is not null) return null;
            if (IsLegacy(src) || target is "rtf" or "odt")
            {
                return src != "pages" && OfficeEngine.IsInstalled(OfficeApp.Word) ? null : ExternalTool.LibreOffice;
            }
        }
        return null;
    }
}
