using WinKame.Core;
using WinKame.Core.Engines;
using WinKame.Dialogs;
using WinKame.Jobs;

namespace WinKame.Actions;

internal static class PdfActions
{
    private static string Count(IReadOnlyList<string> files) => Ui.Plural(files.Count, "PDF", "PDFs");

    public static Task Compress(IReadOnlyList<string> files)
    {
        var hasGs = ToolLocator.Find(ExternalTool.Ghostscript) is not null;
        var v = FormDialog.Show("Comprimir PDF", Count(files),
        [
            new ChoiceField("l", "Nível", ["Alta qualidade", "Equilibrado", "Menor arquivo"], 1),
            new NoteField(hasGs
                ? "Usando Ghostscript."
                : "Sem Ghostscript, só as fotos do PDF são recomprimidas. Instale o Ghostscript em Configurações › Ferramentas para resultados bem menores."),
        ], "Comprimir");
        if (v is null) return Task.CompletedTask;
        var level = (PdfCompressLevel)v.Choice("l");
        JobRunner.ForEach("Comprimindo PDF", files, async (f, ctx, _) => [await PdfEngine.CompressAsync(f, level, ctx.Token)]);
        return Task.CompletedTask;
    }

    public static Task Merge(IReadOnlyList<string> files)
    {
        var ordered = OrderDialog.Show("Juntar PDFs", "Os PDFs serão unidos nesta ordem.", files, "Juntar");
        if (ordered is null) return Task.CompletedTask;
        JobRunner.Start("Juntando PDFs", ctx =>
        {
            ctx.Output([PdfEngine.Merge(ordered)]);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    public static Task Split(IReadOnlyList<string> files)
    {
        JobRunner.ForEach("Dividindo páginas", files, (f, _, p) => Task.FromResult<IEnumerable<string>>([PdfEngine.Split(f, p)]));
        return Task.CompletedTask;
    }

    public static async Task Organize(IReadOnlyList<string> files)
    {
        var ops = await PdfOrganizeDialog.Show(files[0]);
        if (ops is null) return;
        JobRunner.Start("Organizando páginas", ctx =>
        {
            ctx.Output([PdfEngine.Organize(files[0], ops)]);
            return Task.CompletedTask;
        });
    }

    public static Task Rotate(IReadOnlyList<string> files)
    {
        var v = FormDialog.Show("Girar páginas", Count(files),
        [
            new ChoiceField("d", "Girar", ["90° horário", "180°", "90° anti-horário"]),
        ], "Girar");
        if (v is null) return Task.CompletedTask;
        var deg = v.Choice("d") switch { 0 => 90, 1 => 180, _ => 270 };
        JobRunner.ForEach("Girando PDF", files, (f, _, _) => Task.FromResult<IEnumerable<string>>([PdfEngine.RotateAll(f, deg)]));
        return Task.CompletedTask;
    }

    public static Task ExtractPages(IReadOnlyList<string> files)
    {
        var count = files.Count == 1 ? PdfEngine.PageCount(files[0]) : 0;
        var v = FormDialog.Show("Extrair páginas", files.Count == 1 ? System.IO.Path.GetFileName(files[0]) + " · " + T("{0} páginas", count) : Count(files),
        [
            new TextField("r", "Páginas", "1", "Ex.: 1-3, 5, 8- (do 8 até o fim)"),
        ], "Extrair", validate: vals =>
        {
            if (count == 0) return null;
            try { PdfEngine.ParsePageRanges(vals.Text("r"), count); return null; }
            catch (Exception ex) { return ex.Message; }
        });
        if (v is null) return Task.CompletedTask;
        var ranges = v.Text("r");
        JobRunner.ForEach("Extraindo páginas", files, (f, _, _) => Task.FromResult<IEnumerable<string>>([PdfEngine.ExtractPages(f, ranges)]));
        return Task.CompletedTask;
    }

    public static Task Metadata(IReadOnlyList<string> files)
    {
        var cur = files.Count == 1 ? PdfEngine.ReadMetadata(files[0]) : new PdfMetadata("", "", "", "");
        var v = FormDialog.Show("Metadados do PDF", Count(files),
        [
            new TextField("title", "Título", cur.Title),
            new TextField("author", "Autor", cur.Author),
            new TextField("subject", "Assunto", cur.Subject),
            new TextField("keywords", "Palavras-chave", cur.Keywords),
            new CheckField("strip", "Remover todos os metadados (ignora os campos acima)"),
        ], "Salvar");
        if (v is null) return Task.CompletedTask;
        PdfMetadata? meta = v.Check("strip") ? null : new PdfMetadata(v.Text("title"), v.Text("author"), v.Text("subject"), v.Text("keywords"));
        JobRunner.ForEach(meta is null ? "Removendo metadados" : "Salvando metadados", files,
            (f, _, _) => Task.FromResult<IEnumerable<string>>([PdfEngine.WriteMetadata(f, meta)]));
        return Task.CompletedTask;
    }
}
