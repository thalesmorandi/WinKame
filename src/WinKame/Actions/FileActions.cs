using System.IO;
using WinKame.Core.Engines;
using WinKame.Jobs;
using WinKame.Shell;

namespace WinKame.Actions;

internal static class FileActions
{
    private static Task Done => Task.CompletedTask;

    public static Task Copy(IReadOnlyList<string> files)
    {
        FileOps.CopyToClipboard(files);
        App.Instance.Toast($"{Ui.Plural(files.Count, "item copiado", "itens copiados")}", "Cole com Ctrl+V no Explorer");
        return Done;
    }

    public static Task CopyTo(IReadOnlyList<string> files)
    {
        FileOps.Transfer(files, move: false);
        return Done;
    }

    public static Task MoveTo(IReadOnlyList<string> files)
    {
        FileOps.Transfer(files, move: true);
        return Done;
    }

    public static Task CopyPath(IReadOnlyList<string> files)
    {
        FileOps.CopyPaths(files);
        App.Instance.Toast(files.Count == 1 ? "Caminho copiado" : T("{0} caminhos copiados", files.Count), files.Count == 1 ? files[0] : "Um por linha");
        return Done;
    }

    public static Task Terminal(IReadOnlyList<string> files)
    {
        FileOps.OpenTerminal(files[0]);
        return Done;
    }

    public static Task Shelf(IReadOnlyList<string> files)
    {
        App.Instance.Shelf.AddFiles(files);
        return Done;
    }

    public static Task Share(IReadOnlyList<string> files)
    {
        try
        {
            ShareHelper.Share(files);
        }
        catch (Exception ex)
        {
            Ui.Error(T("Não foi possível abrir o compartilhamento do Windows: {0}", ex.Message));
        }
        return Done;
    }

    public static Task OpenWith(IReadOnlyList<string> files)
    {
        FileOps.OpenWith(files[0]);
        return Done;
    }

    public static Task Zip(IReadOnlyList<string> files)
    {
        JobRunner.Start("Compactando", ctx =>
        {
            ctx.Detail(files.Count == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : T("{0} itens", files.Count));
            ctx.Output([ArchiveEngine.ZipItems(files, ctx.Progress(), ctx.Token)]);
            ctx.Detail("");
            return Task.CompletedTask;
        });
        return Done;
    }

    public static Task Extract(IReadOnlyList<string> files)
    {
        JobRunner.ForEach("Extraindo", files, (f, ctx, p) => Task.FromResult<IEnumerable<string>>([ArchiveEngine.Extract(f, p, ctx.Token)]));
        return Done;
    }
}
