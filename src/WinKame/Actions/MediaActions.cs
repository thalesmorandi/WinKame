using WinKame.Core;
using WinKame.Core.Engines;
using WinKame.Dialogs;
using WinKame.Jobs;
using WinKame.Shell;

namespace WinKame.Actions;

internal static class MediaActions
{
    private static Task<bool> Ready() => ToolGate.EnsureAsync(ExternalTool.FFmpeg);

    private static string Count(IReadOnlyList<string> files) => Ui.Plural(files.Count, "arquivo", "arquivos");

    private static void Each(string title, IReadOnlyList<string> files, Func<string, IProgress<double>, CancellationToken, Task<string>> work) =>
        JobRunner.ForEach(title, files, async (f, ctx, p) => [await work(f, p, ctx.Token)]);

    private static void EachMany(string title, IReadOnlyList<string> files, Func<string, IProgress<double>, CancellationToken, Task<IReadOnlyList<string>>> work) =>
        JobRunner.ForEach(title, files, async (f, ctx, p) => await work(f, p, ctx.Token));

    private static readonly int[] Heights = [0, 1080, 720, 480];

    public static async Task CompressVideo(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Comprimir vídeo", Count(files),
        [
            new ChoiceField("level", "Compressão", ["Leve", "Média", "Forte"], 1),
            new ChoiceField("res", "Resolução máxima", ["Original", "1080p", "720p", "480p"]),
            new NoteField("Salva um MP4 (H.264) ao lado do original."),
        ], "Comprimir");
        if (v is null) return;
        var level = (CompressLevel)v.Choice("level");
        var h = Heights[v.Choice("res")];
        Each("Comprimindo vídeo", files, (f, p, ct) => MediaEngine.CompressVideoAsync(f, level, h, p, ct));
    }

    public static async Task Trim(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        TimeSpan start, end;
        var silence = false;
        if (files.Count == 1)
        {
            var r = await MediaTimelineDialog.PickRange(files[0], "Cortar trecho", "Cortar");
            if (r is null) return;
            (start, end, silence) = r.Value;
        }
        else
        {
            var v = FormDialog.Show("Cortar trecho", Count(files) + " · " + T("o mesmo trecho em todos"),
            [
                new TextField("s", "Início", "0:00", "Ex.: 0:05, 1:30 ou 1m30s"),
                new TextField("e", "Fim", "0:30"),
                new CheckField("sil", "Remover silêncio nas pontas (áudio)"),
            ], "Cortar", validate: vals =>
                MediaEngine.TryParseTime(vals.Text("s"), out var a) && MediaEngine.TryParseTime(vals.Text("e"), out var b) && b > a
                    ? null : "Use tempos como 0:05 e 1:30, com o fim depois do início.");
            if (v is null) return;
            MediaEngine.TryParseTime(v.Text("s"), out start);
            MediaEngine.TryParseTime(v.Text("e"), out end);
            silence = v.Check("sil");
        }
        Each("Cortando", files, (f, p, ct) => MediaEngine.TrimAsync(f, start, end, silence, p, ct));
    }

    public static async Task Split(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        if (files.Count == 1)
        {
            var points = await MediaTimelineDialog.PickPoints(files[0], "Dividir", "Dividir");
            if (points is null || points.Count == 0) return;
            EachMany("Dividindo", files, (f, p, ct) => MediaEngine.SplitAsync(f, points, p, ct));
            return;
        }
        var v = FormDialog.Show("Dividir", Count(files),
        [
            new TextField("every", "Dividir a cada", "60", "Segundos, ou um tempo como 1:30"),
        ], "Dividir", validate: vals => MediaEngine.TryParseTime(vals.Text("every"), out var t) && t.TotalSeconds >= 1 ? null : "Informe um intervalo válido.");
        if (v is null) return;
        MediaEngine.TryParseTime(v.Text("every"), out var step);
        EachMany("Dividindo", files, async (f, p, ct) => await MediaEngine.SplitAsync(f, await MediaEngine.EveryAsync(f, step, ct), p, ct));
    }

    public static async Task Join(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var ordered = OrderDialog.Show("Juntar", "Os arquivos serão unidos nesta ordem.", files, "Juntar");
        if (ordered is null) return;
        JobRunner.Start("Juntando", async ctx => ctx.Output([await MediaEngine.JoinAsync(ordered, ctx.Progress(), ctx.Token)]));
    }

    public static async Task Mute(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        Each("Removendo áudio", files, MediaEngine.MuteAsync);
    }

    private static readonly double[] Speeds = [0.25, 0.5, 0.75, 1.25, 1.5, 2, 3];

    public static async Task Speed(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Velocidade", Count(files) + " · " + T("o tom do áudio é mantido"),
        [
            new ChoiceField("s", "Velocidade", Speeds.Select(s => s + "×").ToArray(), 4),
        ], "Aplicar");
        if (v is null) return;
        var factor = Speeds[v.Choice("s")];
        Each("Mudando a velocidade", files, (f, p, ct) => MediaEngine.SpeedAsync(f, factor, p, ct));
    }

    public static async Task Snapshots(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        IReadOnlyList<TimeSpan>? times;
        if (files.Count == 1)
        {
            times = await MediaTimelineDialog.PickPoints(files[0], "Salvar quadros", "Salvar PNG");
        }
        else
        {
            var v = FormDialog.Show("Salvar quadros", Count(files),
            [
                new TextField("t", "Tempos", "0:01", "Separados por vírgula, ex.: 0:01, 0:10, 1:00"),
            ], "Salvar PNG", validate: vals =>
            {
                try { return MediaEngine.ParseTimes(vals.Text("t")).Count > 0 ? null : "Informe pelo menos um tempo."; }
                catch (FormatException ex) { return ex.Message; }
            });
            times = v is null ? null : MediaEngine.ParseTimes(v.Text("t"));
        }
        if (times is null || times.Count == 0) return;
        EachMany("Salvando quadros", files, (f, p, ct) => MediaEngine.SnapshotsAsync(f, times, p, ct));
    }

    private static readonly string[] VideoAspects = ["1:1", "4:5", "9:16", "16:9", "4:3"];

    public static async Task CropVideo(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Recortar vídeo", Count(files) + " · " + T("recorte centralizado"),
        [
            new ChoiceField("a", "Proporção", ["1:1 quadrado", "4:5 retrato", "9:16 stories", "16:9 paisagem", "4:3"]),
        ], "Recortar");
        if (v is null) return;
        var aspect = VideoAspects[v.Choice("a")];
        Each("Recortando vídeo", files, (f, p, ct) => MediaEngine.CropAspectAsync(f, aspect, p, ct));
    }

    public static async Task RotateVideo(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Girar vídeo", Count(files),
        [
            new ChoiceField("op", "Operação", ["90° horário", "90° anti-horário", "180°", "Espelhar na horizontal", "Espelhar na vertical"]),
        ], "Aplicar");
        if (v is null) return;
        var op = (RotateOp)v.Choice("op");
        Each("Girando vídeo", files, (f, p, ct) => MediaEngine.RotateVideoAsync(f, op, p, ct));
    }

    public static async Task StripMetadata(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        Each("Removendo metadados", files, MediaEngine.StripMetadataAsync);
    }

    private static readonly int[] Bitrates = [64, 96, 128, 160, 192];

    public static async Task CompressAudio(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Comprimir áudio", Count(files),
        [
            new ChoiceField("b", "Taxa de bits", Bitrates.Select(b => b + " kbps").ToArray(), 2),
            new NoteField("WAV, FLAC e AIFF viram MP3. Os outros mantêm o formato."),
        ], "Comprimir");
        if (v is null) return;
        var kbps = Bitrates[v.Choice("b")];
        Each("Comprimindo áudio", files, (f, p, ct) => MediaEngine.CompressAudioAsync(f, kbps, p, ct));
    }

    private static readonly double[] Loudness = [-14, -16, -23];

    public static async Task Normalize(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Normalizar volume", Count(files),
        [
            new ChoiceField("l", "Alvo", ["Streaming (−14 LUFS)", "Podcast (−16 LUFS)", "TV (−23 LUFS)"], 1),
        ], "Normalizar");
        if (v is null) return;
        var target = Loudness[v.Choice("l")];
        Each("Normalizando", files, (f, p, ct) => MediaEngine.NormalizeAsync(f, target, p, ct));
    }

    public static async Task Channels(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Canais de áudio", Count(files),
        [
            new ChoiceField("c", "Converter para", ["Mono", "Estéreo"]),
        ], "Converter");
        if (v is null) return;
        var ch = v.Choice("c") == 0 ? 1 : 2;
        Each("Convertendo canais", files, (f, p, ct) => MediaEngine.ChannelsAsync(f, ch, p, ct));
    }

    public static async Task Visualize(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Visualizar áudio", Count(files) + " · " + T("cria um MP4 com a forma de onda"),
        [
            new ChoiceField("shape", "Formato", ["Paisagem 16:9", "Retrato 9:16", "Quadrado"]),
            new ColorField("color", "Cor da onda", "#FF8A1F"),
            new ColorField("bg", "Fundo", "#000000"),
        ], "Criar vídeo");
        if (v is null) return;
        var shape = (VisualizeShape)v.Choice("shape");
        string Hex(string s) => "0x" + s.TrimStart('#')[..Math.Min(6, s.TrimStart('#').Length)];
        var color = Hex(v.Text("color"));
        var bg = Hex(v.Text("bg"));
        Each("Criando vídeo", files, (f, p, ct) => MediaEngine.VisualizeAsync(f, shape, color, bg, p, ct));
    }

    public static async Task Bleep(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        var v = FormDialog.Show("Bipe", System.IO.Path.GetFileName(files[0]),
        [
            new TextField("r", "Trechos para cobrir", "0:10-0:12", "Início-fim separados por vírgula, ex.: 0:10-0:12, 1:05-1:06", Multiline: true),
        ], "Aplicar bipe", validate: vals =>
        {
            try { return MediaEngine.ParseRanges(vals.Text("r")).Count > 0 ? null : "Informe pelo menos um trecho."; }
            catch (FormatException ex) { return ex.Message; }
        });
        if (v is null) return;
        var ranges = MediaEngine.ParseRanges(v.Text("r"));
        Each("Aplicando bipe", files, (f, p, ct) => MediaEngine.BleepAsync(f, ranges, p, ct));
    }

    public static async Task Tags(IReadOnlyList<string> files)
    {
        if (!await Ready()) return;
        IReadOnlyDictionary<string, string> current = new Dictionary<string, string>();
        if (files.Count == 1)
        {
            try { current = (await MediaEngine.ProbeAsync(files[0])).Tags; }
            catch { /* start empty */ }
        }
        string Cur(string k) => current.TryGetValue(k, out var s) ? s : "";
        var keys = new[] { ("title", "Título"), ("artist", "Artista"), ("album", "Álbum"), ("album_artist", "Artista do álbum"), ("genre", "Gênero"), ("date", "Ano"), ("track", "Faixa"), ("comment", "Comentário") };
        var fields = new List<Field>();
        if (files.Count > 1) fields.Add(new NoteField("Campos vazios não são alterados."));
        fields.AddRange(keys.Select(k => (Field)new TextField(k.Item1, k.Item2, Cur(k.Item1))));
        fields.Add(new CheckField("clear", "Remover todos os outros metadados"));
        var v = FormDialog.Show("Tags", Count(files), fields, "Salvar");
        if (v is null) return;
        var tags = keys.Select(k => (k.Item1, v.Text(k.Item1).Trim()))
            .Where(t => files.Count == 1 || t.Item2.Length > 0)
            .ToDictionary(t => t.Item1, t => t.Item2);
        var clear = v.Check("clear");
        Each("Salvando tags", files, (f, p, ct) => MediaEngine.SetTagsAsync(f, tags, clear, p, ct));
    }
}
