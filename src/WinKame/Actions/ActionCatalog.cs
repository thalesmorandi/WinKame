using System.IO;
using WinKame.Core;
using WinKame.Jobs;
using WinKame.Wheel;

namespace WinKame.Actions;

/// <summary>Builds the slices of each wheel for the files being dragged.</summary>
internal static class ActionCatalog
{
    // Segoe Fluent / MDL2 icon code points.
    internal static class G
    {
        public const string Compress = "";
        public const string Resize = "";
        public const string Crop = "";
        public const string Rotate = "";
        public const string Adjust = "";
        public const string Background = "";
        public const string Hide = "";
        public const string Tag = "";
        public const string Pdf = "";
        public const string Tiles = "";
        public const string Cut = "";
        public const string Split = "";
        public const string Link = "";
        public const string Mute = "";
        public const string Speed = "";
        public const string Camera = "";
        public const string Equalizer = "";
        public const string Speakers = "";
        public const string Wave = "";
        public const string Mic = "";
        public const string Sort = "";
        public const string Pages = "";
        public const string Folder = "";
        public const string Zip = "";
        public const string Copy = "";
        public const string CopyTo = "";
        public const string Move = "";
        public const string Path = "";
        public const string Terminal = "";
        public const string Pin = "";
        public const string Share = "";
        public const string OpenWith = "";
        public const string Bolt = "";
        public const string Add = "";
    }

    private static WheelItem Item(string label, string glyph, string hint, Func<IReadOnlyList<string>, Task> run) =>
        new(label, glyph, run, hint);

    public static IReadOnlyList<WheelItem> Build(WheelKind kind, IReadOnlyList<string> files, AppSettings settings)
    {
        var items = kind switch
        {
            WheelKind.Convert => ConvertItems(files),
            WheelKind.Tools => ToolItems(files),
            WheelKind.Actions => FileActionItems(files),
            _ => [],
        };
        items.AddRange(CustomActions.ItemsFor(kind, files, settings));
        if (kind == WheelKind.Custom && items.Count == 0)
        {
            items.Add(Item("Nova ação", G.Add, "Criar uma ação personalizada em Configurações", _ =>
            {
                App.Instance.OpenSettings("custom");
                return Task.CompletedTask;
            }));
        }
        return items;
    }

    // ---------- convert ----------

    private static List<WheelItem> ConvertItems(IReadOnlyList<string> files)
    {
        return Formats.CommonTargets(files)
            .Select(t => new WheelItem(Formats.Label(t), "", f => ConvertAll(f, t), T("Converter para {0}", Formats.Label(t)), IsFormat: true))
            .ToList();
    }

    private static async Task ConvertAll(IReadOnlyList<string> files, string target)
    {
        var todo = files.Where(f => Formats.Canonical(Formats.Ext(f)) != target).ToList();
        foreach (var tool in todo.Select(f => Converter.RequiredTool(f, target)).OfType<ExternalTool>().Distinct())
        {
            if (!await Shell.ToolGate.EnsureAsync(tool)) return;
        }
        JobRunner.ForEach(T("Convertendo para {0}", Formats.Label(target)), todo,
            async (f, ctx, p) => await Converter.ConvertAsync(f, target, p, ctx.Token));
    }

    // ---------- tools ----------

    private static List<WheelItem> ToolItems(IReadOnlyList<string> files)
    {
        var kind = Formats.CommonKind(files);
        var many = files.Count > 1;
        var list = new List<WheelItem>();
        switch (kind)
        {
            case FileKind.Image:
                list.Add(Item("Comprimir", G.Compress, "Reduzir o tamanho do arquivo", ImageActions.Compress));
                list.Add(Item("Redimensionar", G.Resize, "Mudar as dimensões", ImageActions.Resize));
                list.Add(Item("Recortar", G.Crop, "Recortar com proporção ou tamanho exato", ImageActions.Crop));
                list.Add(Item("Girar", G.Rotate, "Girar ou espelhar", ImageActions.Rotate));
                list.Add(Item("Editar", G.Adjust, "Brilho, contraste, cor e nitidez", ImageActions.Adjust));
                list.Add(Item("Fundo", G.Background, "Colocar a foto sobre uma cor ou gradiente", ImageActions.Background));
                if (!many) list.Add(Item("Ocultar", G.Hide, "Desfocar ou cobrir áreas privadas", ImageActions.Redact));
                list.Add(Item("Metadados", G.Tag, "Remover EXIF, GPS e outros metadados", ImageActions.StripMetadata));
                if (many)
                {
                    list.Add(Item("Criar PDF", G.Pdf, "Uma página por imagem", ImageActions.ToPdf));
                    list.Add(Item("Colagem", G.Tiles, "Juntar em grade, linha ou coluna", ImageActions.Collage));
                }
                break;

            case FileKind.Video:
                list.Add(Item("Comprimir", G.Compress, "Reduzir o tamanho do vídeo", MediaActions.CompressVideo));
                list.Add(Item("Cortar", G.Cut, "Manter só um trecho", MediaActions.Trim));
                list.Add(Item("Dividir", G.Split, "Dividir em partes", MediaActions.Split));
                if (many) list.Add(Item("Juntar", G.Link, "Juntar os vídeos em um MP4", MediaActions.Join));
                list.Add(Item("Sem áudio", G.Mute, "Salvar uma cópia muda", MediaActions.Mute));
                list.Add(Item("Velocidade", G.Speed, "Acelerar ou desacelerar", MediaActions.Speed));
                list.Add(Item("Quadros", G.Camera, "Salvar quadros como PNG", MediaActions.Snapshots));
                list.Add(Item("Recortar", G.Crop, "Mudar a proporção (1:1, 9:16…)", MediaActions.CropVideo));
                list.Add(Item("Girar", G.Rotate, "Girar ou espelhar o vídeo", MediaActions.RotateVideo));
                list.Add(Item("Metadados", G.Tag, "Remover metadados", MediaActions.StripMetadata));
                break;

            case FileKind.Audio:
                list.Add(Item("Comprimir", G.Compress, "Reduzir a taxa de bits", MediaActions.CompressAudio));
                list.Add(Item("Normalizar", G.Equalizer, "Igualar o volume", MediaActions.Normalize));
                list.Add(Item("Cortar", G.Cut, "Manter só um trecho", MediaActions.Trim));
                list.Add(Item("Dividir", G.Split, "Dividir em partes", MediaActions.Split));
                if (many) list.Add(Item("Juntar", G.Link, "Juntar em um arquivo", MediaActions.Join));
                list.Add(Item("Canais", G.Speakers, "Mono ou estéreo", MediaActions.Channels));
                list.Add(Item("Velocidade", G.Speed, "Acelerar ou desacelerar", MediaActions.Speed));
                list.Add(Item("Visualizar", G.Wave, "Criar um MP4 com a forma de onda", MediaActions.Visualize));
                if (!many) list.Add(Item("Bipe", G.Mic, "Cobrir trechos com um bipe", MediaActions.Bleep));
                list.Add(Item("Tags", G.Tag, "Editar artista, álbum, título…", MediaActions.Tags));
                break;

            case FileKind.Pdf:
                list.Add(Item("Comprimir", G.Compress, "Reduzir o tamanho do PDF", PdfActions.Compress));
                if (many) list.Add(Item("Juntar", G.Link, "Juntar os PDFs na ordem escolhida", PdfActions.Merge));
                list.Add(Item("Dividir", G.Split, "Uma página por arquivo", PdfActions.Split));
                if (!many) list.Add(Item("Organizar", G.Sort, "Reordenar, girar, duplicar ou remover páginas", PdfActions.Organize));
                list.Add(Item("Girar", G.Rotate, "Girar todas as páginas", PdfActions.Rotate));
                list.Add(Item("Extrair", G.Pages, "Salvar só algumas páginas", PdfActions.ExtractPages));
                list.Add(Item("Metadados", G.Tag, "Editar ou remover título, autor…", PdfActions.Metadata));
                break;

            case FileKind.Archive:
                list.Add(Item("Extrair", G.Folder, "Extrair para uma pasta ao lado", FileActions.Extract));
                break;
        }
        return list;
    }

    // ---------- file actions ----------

    private static List<WheelItem> FileActionItems(IReadOnlyList<string> files)
    {
        var list = new List<WheelItem>
        {
            Item("Copiar", G.Copy, "Copiar para a área de transferência", FileActions.Copy),
            Item("Copiar para", G.CopyTo, "Copiar para outra pasta", FileActions.CopyTo),
            Item("Mover para", G.Move, "Mover para outra pasta", FileActions.MoveTo),
            Item("Caminho", G.Path, "Copiar o caminho completo", FileActions.CopyPath),
            Item("Terminal", G.Terminal, "Abrir o terminal nesta pasta", FileActions.Terminal),
            Item("Prateleira", G.Pin, "Guardar numa prateleira flutuante", FileActions.Shelf),
            Item("Compartilhar", G.Share, "Compartilhamento do Windows", FileActions.Share),
        };
        if (files.Count == 1 && File.Exists(files[0])) list.Add(Item("Abrir com", G.OpenWith, "Escolher o aplicativo", FileActions.OpenWith));
        list.Add(Item("ZIP", G.Zip, "Compactar em ZIP", FileActions.Zip));
        return list;
    }
}
