// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.IO;
using ImageMagick;
using WinKame.Core;
using WinKame.Core.Engines;
using WinKame.Dialogs;
using WinKame.Jobs;

namespace WinKame.Actions;

internal static class ImageActions
{
    private static readonly string[] AspectLabels = ["1:1", "4:3", "3:2", "16:9", "3:4", "4:5", "9:16"];

    private static void Each(string title, IReadOnlyList<string> files, Func<string, string> work) =>
        JobRunner.ForEach(title, files, (f, _, _) => Task.FromResult<IEnumerable<string>>([work(f)]));

    private static string Count(IReadOnlyList<string> files) => Ui.Plural(files.Count, "imagem", "imagens");

    public static Task Compress(IReadOnlyList<string> files)
    {
        var v = FormDialog.Show("Comprimir", Count(files),
        [
            new SliderField("q", "Qualidade", 10, 100, 75, 1, "%"),
            new NumberField("kb", "Tamanho alvo, 0 = usar a qualidade", 0, 0, 500_000, "KB"),
            new NumberField("max", "Lado maior no máximo, 0 = manter", 0, 0, 20_000, "px"),
            new CheckField("jpg", "Salvar como JPG"),
            new NoteField("PNG é reduzido para 256 cores. Os originais não são alterados."),
        ], "Comprimir");
        if (v is null) return Task.CompletedTask;
        var o = new ImageCompressOptions(v.Int("q"), v.Int("max"), v.Int("kb"), v.Check("jpg"));
        Each("Comprimindo imagens", files, f => ImageEngine.Compress(f, o));
        return Task.CompletedTask;
    }

    public static Task Resize(IReadOnlyList<string> files)
    {
        var v = FormDialog.Show("Redimensionar", Count(files),
        [
            new SliderField("pct", "Escala", 5, 200, 50, 1, "%"),
            new NumberField("w", "Ou largura máxima, 0 = ignorar", 0, 0, 20_000, "px"),
            new NumberField("h", "Altura máxima, 0 = ignorar", 0, 0, 20_000, "px"),
            new NoteField("Com largura ou altura preenchida, a escala é ignorada e a proporção é mantida."),
        ], "Redimensionar");
        if (v is null) return Task.CompletedTask;
        var o = new ImageResizeOptions(v.Num("pct"), v.Int("w"), v.Int("h"));
        Each("Redimensionando", files, f => ImageEngine.Resize(f, o));
        return Task.CompletedTask;
    }

    public static Task Crop(IReadOnlyList<string> files)
    {
        if (files.Count == 1)
        {
            var r = ImageRegionDialog.Crop(files[0]);
            if (r is null) return Task.CompletedTask;
            var (rect, w, h) = r.Value;
            Each("Recortando", files, f => ImageEngine.Crop(f, rect, null, w, h));
            return Task.CompletedTask;
        }
        var v = FormDialog.Show("Recortar", Count(files) + " · " + T("recorte centralizado"),
        [
            new ChoiceField("aspect", "Proporção", AspectLabels),
            new NumberField("w", "Largura de saída, 0 = manter", 0, 0, 20_000, "px"),
            new NumberField("h", "Altura de saída, 0 = manter", 0, 0, 20_000, "px"),
        ], "Recortar");
        if (v is null) return Task.CompletedTask;
        var aspect = AspectLabels[v.Choice("aspect")];
        Each("Recortando", files, f => ImageEngine.Crop(f, null, aspect, v.Int("w"), v.Int("h")));
        return Task.CompletedTask;
    }

    public static Task Rotate(IReadOnlyList<string> files)
    {
        var v = FormDialog.Show("Girar", Count(files),
        [
            new ChoiceField("op", "Operação", ["90° horário", "90° anti-horário", "180°", "Espelhar na horizontal", "Espelhar na vertical"]),
        ], "Aplicar");
        if (v is null) return Task.CompletedTask;
        var op = (RotateOp)v.Choice("op");
        Each("Girando", files, f => ImageEngine.Rotate(f, op));
        return Task.CompletedTask;
    }

    private static AdjustOptions ReadAdjust(FormValues v) => new(
        v.Num("bright"), v.Num("contrast"), v.Num("sat"), v.Num("hue"), v.Num("sharp"), v.Num("blur"),
        v.Check("gray"), v.Check("sepia"), v.Check("auto"));

    /// <summary>Preview helper: works on a small copy of the first file.</summary>
    private static Func<FormValues, CancellationToken, Task<byte[]?>> Preview(string file, Func<MagickImage, FormValues, MagickImage> render)
    {
        MagickImage? baseImg = null;
        var gate = new object();
        return (v, ct) => Task.Run<byte[]?>(() =>
        {
            lock (gate)
            {
                baseImg ??= ImageEngine.LoadPreview(file, 700);
                ct.ThrowIfCancellationRequested();
                using var copy = (MagickImage)baseImg.Clone();
                var result = render(copy, v);
                try { return ImageEngine.ToPng(result); }
                finally { if (!ReferenceEquals(result, copy)) result.Dispose(); }
            }
        }, ct);
    }

    public static Task Adjust(IReadOnlyList<string> files)
    {
        var v = FormDialog.Show("Editar", Count(files),
        [
            new SliderField("bright", "Brilho", -60, 60, 0),
            new SliderField("contrast", "Contraste", -60, 60, 0),
            new SliderField("sat", "Saturação", 0, 200, 100, 1, "%"),
            new SliderField("hue", "Matiz", 0, 200, 100, 1, "%"),
            new SliderField("sharp", "Nitidez", 0, 5, 0, 0.1),
            new SliderField("blur", "Desfoque", 0, 20, 0, 0.5),
            new CheckField("auto", "Nível automático"),
            new CheckField("gray", "Preto e branco"),
            new CheckField("sepia", "Sépia"),
        ], "Salvar cópias", Preview(files[0], (img, vals) =>
        {
            ImageEngine.ApplyAdjust(img, ReadAdjust(vals));
            return img;
        }));
        if (v is null) return Task.CompletedTask;
        var o = ReadAdjust(v);
        Each("Editando", files, f => ImageEngine.Adjust(f, o));
        return Task.CompletedTask;
    }

    private static readonly string[] BgAspects = ["original", "1:1", "4:5", "16:9", "9:16", "4:3"];

    private static BackgroundOptions ReadBackground(FormValues v) => new(
        v.Text("c1"),
        v.Check("grad") ? v.Text("c2") : null,
        BgAspects[v.Choice("aspect")],
        v.Num("pad"),
        v.Num("corner"),
        v.Check("shadow"));

    public static Task Background(IReadOnlyList<string> files)
    {
        var v = FormDialog.Show("Adicionar fundo", Count(files),
        [
            new ColorField("c1", "Cor", "#FF8A1F"),
            new CheckField("grad", "Gradiente", true),
            new ColorField("c2", "Segunda cor", "#E5487A"),
            new ChoiceField("aspect", "Proporção", ["Original", "1:1", "4:5", "16:9", "9:16", "4:3"]),
            new SliderField("pad", "Margem", 0, 40, 10, 1, "%"),
            new SliderField("corner", "Cantos arredondados", 0, 20, 4, 1, "%"),
            new CheckField("shadow", "Sombra", true),
        ], "Salvar", Preview(files[0], (img, vals) => ImageEngine.RenderBackground(img, ReadBackground(vals))));
        if (v is null) return Task.CompletedTask;
        var o = ReadBackground(v);
        Each("Adicionando fundo", files, f => ImageEngine.AddBackground(f, o));
        return Task.CompletedTask;
    }

    public static Task Redact(IReadOnlyList<string> files)
    {
        var r = ImageRegionDialog.Redact(files[0]);
        if (r is null) return Task.CompletedTask;
        var (areas, mode) = r.Value;
        Each("Ocultando áreas", files, f => ImageEngine.Redact(f, areas, mode));
        return Task.CompletedTask;
    }

    public static Task StripMetadata(IReadOnlyList<string> files)
    {
        Each("Removendo metadados", files, ImageEngine.StripMetadata);
        return Task.CompletedTask;
    }

    public static Task ToPdf(IReadOnlyList<string> files)
    {
        var ordered = OrderDialog.Show("Criar PDF", "Uma página por imagem, nesta ordem.", files, "Criar PDF");
        if (ordered is null) return Task.CompletedTask;
        JobRunner.Start("Criando PDF", ctx =>
        {
            var outPath = OutputPaths.Unique(OutputPaths.DirFor(ordered[0]), OutputPaths.StemOf(ordered[0]), "pdf");
            ImageEngine.ToPdf(ordered, outPath);
            ctx.Output([outPath]);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    private static readonly string[] Layouts = ["grid", "row", "column"];

    private static CollageOptions ReadCollage(FormValues v) =>
        new(Layouts[v.Choice("layout")], v.Int("cell"), v.Int("gap"), v.Int("corner"), v.Text("bg"));

    public static Task Collage(IReadOnlyList<string> files)
    {
        var ordered = OrderDialog.Show("Colagem", "Escolha a ordem das imagens.", files, "Continuar");
        if (ordered is null) return Task.CompletedTask;

        // Small copies keep the live preview quick.
        var thumbsDir = OutputPaths.NewTempDir();
        List<string>? thumbs = null;
        var gate = new object();
        List<string> Thumbs()
        {
            lock (gate)
            {
                return thumbs ??= ordered.Select((f, i) =>
                {
                    using var t = ImageEngine.LoadPreview(f, 360);
                    var p = Path.Combine(thumbsDir, $"{i:000}.png");
                    t.Write(p, MagickFormat.Png);
                    return p;
                }).ToList();
            }
        }

        try
        {
            var v = FormDialog.Show("Colagem", Ui.Plural(ordered.Count, "imagem", "imagens"),
            [
                new ChoiceField("layout", "Layout", ["Grade", "Linha", "Coluna"]),
                new SliderField("cell", "Tamanho de cada imagem", 200, 1600, 600, 10, " px"),
                new SliderField("gap", "Espaçamento", 0, 120, 16, 1, " px"),
                new SliderField("corner", "Cantos", 0, 120, 0, 1, " px"),
                new ColorField("bg", "Fundo", "#FFFFFF"),
            ], "Criar colagem", (vals, ct) => Task.Run<byte[]?>(() =>
            {
                var o = ReadCollage(vals);
                using var img = ImageEngine.RenderCollage(Thumbs(), o, scaleDown: Math.Max(1, o.Cell / 180));
                return ImageEngine.ToPng(img);
            }, ct));
            if (v is null) return Task.CompletedTask;
            var opts = ReadCollage(v);
            JobRunner.Start("Criando colagem", ctx =>
            {
                ctx.Output([ImageEngine.Collage(ordered, opts)]);
                return Task.CompletedTask;
            });
        }
        finally
        {
            OutputPaths.TryDelete(thumbsDir);
        }
        return Task.CompletedTask;
    }
}
