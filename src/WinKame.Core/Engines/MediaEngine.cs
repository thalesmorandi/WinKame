using System.Globalization;
using System.Text.RegularExpressions;

namespace WinKame.Core.Engines;

public sealed record MediaInfo(TimeSpan Duration, bool HasVideo, bool HasAudio, int Width, int Height, IReadOnlyDictionary<string, string> Tags);

public enum CompressLevel { Light, Medium, Strong }

public enum VisualizeShape { Landscape, Portrait, Square }

/// <summary>Audio and video work, all through ffmpeg.</summary>
public static partial class MediaEngine
{
    private static readonly HashSet<string> AudioOnly = ["mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "aiff", "wma", "amr", "mka"];

    public static bool IsAudioExt(string ext) => AudioOnly.Contains(Formats.Canonical(ext));

    private static string Ff => ToolLocator.Require(ExternalTool.FFmpeg);

    // ---------- probing ----------

    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationRx();

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video: .*?(\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoRx();

    [GeneratedRegex(@"^\s{4}(\w+)\s*:\s(.*)$")]
    private static partial Regex TagRx();

    public static async Task<MediaInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(Ff, ["-hide_banner", "-nostdin", "-i", file], ct, throwOnError: false).ConfigureAwait(false);
        var err = r.StdErr;
        var duration = TimeSpan.Zero;
        var dm = DurationRx().Match(err);
        if (dm.Success)
        {
            duration = new TimeSpan(0, int.Parse(dm.Groups[1].Value), int.Parse(dm.Groups[2].Value), 0)
                + TimeSpan.FromSeconds(double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        bool hasVideo = false, hasAudio = false;
        int w = 0, h = 0;
        foreach (var line in err.Split('\n'))
        {
            if (line.Contains("Video:") && line.Contains("Stream #") && !line.Contains("attached pic"))
            {
                var vm = VideoRx().Match(line);
                if (vm.Success && !hasVideo)
                {
                    w = int.Parse(vm.Groups[1].Value);
                    h = int.Parse(vm.Groups[2].Value);
                }
                hasVideo = true;
            }
            if (line.Contains("Audio:") && line.Contains("Stream #")) hasAudio = true;
        }

        // Tags of the container: the "Metadata:" block right after "Input #0".
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = err.Split('\n');
        var inInput = false;
        var inMeta = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Input #0")) { inInput = true; continue; }
            if (!inInput) continue;
            if (line.StartsWith("  Metadata:")) { inMeta = true; continue; }
            if (inMeta)
            {
                var tm = TagRx().Match(line);
                if (tm.Success) { tags.TryAdd(tm.Groups[1].Value, tm.Groups[2].Value.Trim()); continue; }
                break;
            }
            if (line.StartsWith("  Duration:") || line.StartsWith("  Stream")) break;
        }

        if (duration == TimeSpan.Zero && !hasVideo && !hasAudio)
        {
            throw new InvalidDataException(T("Não foi possível ler a mídia: {0}", Path.GetFileName(file)) + "\n" + ProcessRunner.Tail(err, 4));
        }
        return new MediaInfo(duration, hasVideo, hasAudio, w, h, tags);
    }

    // ---------- runner ----------

    private static async Task RunAsync(List<string> args, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
    {
        var all = new List<string> { "-hide_banner", "-y", "-nostdin", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);
        var total = duration.TotalSeconds;
        await ProcessRunner.RunAsync(Ff, all, ct, onStdOut: line =>
        {
            if (progress is null || total <= 0) return;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                if (long.TryParse(line.AsSpan(line.IndexOf('=') + 1), out var us) && us > 0)
                {
                    progress.Report(Math.Clamp(us / 1_000_000.0 / total, 0, 1));
                }
            }
        }).ConfigureAwait(false);
    }

    // ---------- codec choices ----------

    private static List<string> VideoCodec(string ext) => Formats.Canonical(ext) switch
    {
        "webm" => ["-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p"],
        "avi" => ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4"],
        "wmv" => ["-c:v", "wmv2", "-b:v", "3M"],
        "mov" => ["-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart"],
        "mkv" => ["-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p"],
        _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart"],
    };

    private static List<string> AudioCodec(string ext, int bitrateKbps = 0)
    {
        string B(int def) => $"{(bitrateKbps > 0 ? bitrateKbps : def)}k";
        return Formats.Canonical(ext) switch
        {
            "mp3" => bitrateKbps > 0 ? ["-c:a", "libmp3lame", "-b:a", B(192)] : ["-c:a", "libmp3lame", "-q:a", "2"],
            "m4a" or "aac" or "mp4" or "mov" or "mkv" => ["-c:a", "aac", "-b:a", B(192)],
            "wav" => ["-c:a", "pcm_s16le"],
            "flac" => ["-c:a", "flac"],
            "ogg" => bitrateKbps > 0 ? ["-c:a", "libvorbis", "-b:a", B(160)] : ["-c:a", "libvorbis", "-q:a", "5"],
            "opus" or "webm" => ["-c:a", "libopus", "-b:a", B(128)],
            "aiff" => ["-c:a", "pcm_s16be"],
            "wma" or "wmv" => ["-c:a", "wmav2", "-b:a", B(192)],
            "avi" => ["-c:a", "libmp3lame", "-b:a", B(192)],
            _ => ["-c:a", "aac", "-b:a", B(192)],
        };
    }

    /// <summary>Most encoders with 4:2:0 output need even width and height.</summary>
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    /// <summary>Encoding arguments for an output in <paramref name="ext"/>, with optional filters.</summary>
    private static List<string> Encode(string ext, MediaInfo info, IEnumerable<string>? vf = null, IEnumerable<string>? af = null, int audioBitrate = 0, List<string>? videoCodec = null)
    {
        var args = new List<string>();
        var vFilters = vf?.ToList() ?? [];
        var aFilters = af?.ToList() ?? [];
        if (IsAudioExt(ext))
        {
            args.AddRange(["-map", "0:a:0", "-vn"]);
            if (aFilters.Count > 0) args.AddRange(["-af", string.Join(",", aFilters)]);
            args.AddRange(AudioCodec(ext, audioBitrate));
            return args;
        }

        args.AddRange(["-map", "0:v:0"]);
        if (info.HasAudio) args.AddRange(["-map", "0:a:0?"]);
        vFilters.Add(EvenSize);
        args.AddRange(["-vf", string.Join(",", vFilters)]);
        args.AddRange(videoCodec ?? VideoCodec(ext));
        if (info.HasAudio)
        {
            if (aFilters.Count > 0) args.AddRange(["-af", string.Join(",", aFilters)]);
            args.AddRange(AudioCodec(ext, audioBitrate));
        }
        else
        {
            args.Add("-an");
        }
        return args;
    }

    /// <summary>Video containers we keep when editing; others become MP4.</summary>
    private static string KeepVideoExt(string input)
    {
        var e = Formats.Canonical(Formats.Ext(input));
        return e is "mp4" or "mov" or "mkv" or "webm" ? e : "mp4";
    }

    private static string KeepAudioExt(string input)
    {
        var e = Formats.Canonical(Formats.Ext(input));
        return e is "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" ? e : "m4a";
    }

    private static string KeepExt(string input, MediaInfo info) => info.HasVideo ? KeepVideoExt(input) : KeepAudioExt(input);

    private static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------- conversions ----------

    public static async Task<string> ConvertAsync(string input, string target, IProgress<double>? progress, CancellationToken ct)
    {
        target = Formats.Canonical(target);
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var outPath = OutputPaths.For(input, target);
        List<string> args = ["-i", input];

        if (target == "gif")
        {
            args.AddRange(["-filter_complex", "[0:v]fps=12,scale='min(720,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a", "-loop", "0", "-an"]);
        }
        else if (IsAudioExt(target))
        {
            if (!info.HasAudio) throw new InvalidOperationException(T("Este arquivo não tem áudio."));
            args.AddRange(Encode(target, info));
        }
        else
        {
            if (!info.HasVideo) throw new InvalidOperationException(T("Este arquivo não tem vídeo."));
            args.AddRange(Encode(target, info));
        }
        args.Add(outPath);
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    // ---------- video tools ----------

    public static async Task<string> CompressVideoAsync(string input, CompressLevel level, int maxHeight, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var ext = KeepVideoExt(input);
        var crf = level switch { CompressLevel.Light => 24, CompressLevel.Medium => 28, _ => 32 };
        var vf = new List<string>();
        if (maxHeight > 0 && info.Height > maxHeight) vf.Add($"scale=-2:{maxHeight}");
        List<string> codec = ext == "webm"
            ? ["-c:v", "libvpx-vp9", "-crf", (crf + 8).ToString(), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p"]
            : ["-c:v", "libx264", "-preset", "slow", "-crf", crf.ToString(), "-pix_fmt", "yuv420p", "-movflags", "+faststart"];
        var abr = level switch { CompressLevel.Light => 160, CompressLevel.Medium => 128, _ => 96 };
        var outPath = OutputPaths.For(input, ext, "comprimido");
        List<string> args = ["-i", input, .. Encode(ext, info, vf, audioBitrate: abr, videoCodec: codec), outPath];
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> MuteAsync(string input, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var outPath = OutputPaths.For(input, Formats.Canonical(Formats.Ext(input)), "sem áudio");
        await RunAsync(["-i", input, "-map", "0:v", "-c", "copy", "-an", outPath], info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> TrimAsync(string input, TimeSpan start, TimeSpan end, bool trimSilence, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        if (end <= start || end > info.Duration) end = info.Duration;
        var length = end - start;
        var ext = KeepExt(input, info);
        var af = new List<string>();
        if (trimSilence)
        {
            af.Add("silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1");
            af.Add("areverse");
            af.Add("silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.1");
            af.Add("areverse");
        }
        var outPath = OutputPaths.For(input, ext, "cortado");
        List<string> args = ["-ss", Sec(start.TotalSeconds), "-i", input, "-t", Sec(length.TotalSeconds), .. Encode(ext, info, af: af), outPath];
        await RunAsync(args, length, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<IReadOnlyList<string>> SplitAsync(string input, IReadOnlyList<TimeSpan> points, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var cuts = points.Where(p => p > TimeSpan.Zero && p < info.Duration).Distinct().Order().ToList();
        if (cuts.Count == 0) throw new InvalidOperationException(T("Nenhum ponto de corte dentro da duração."));
        var bounds = new List<TimeSpan> { TimeSpan.Zero };
        bounds.AddRange(cuts);
        bounds.Add(info.Duration);

        var ext = KeepExt(input, info);
        var dir = OutputPaths.FolderFor(input, "partes");
        var stem = OutputPaths.StemOf(input);
        var outputs = new List<string>();
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var s = bounds[i];
            var len = bounds[i + 1] - s;
            var outPath = Path.Combine(dir, $"{stem} - {i + 1:00}.{ext}");
            var part = i;
            var sub = new Progress<double>(v => progress?.Report((part + v) / (bounds.Count - 1)));
            List<string> args = ["-ss", Sec(s.TotalSeconds), "-i", input, "-t", Sec(len.TotalSeconds), .. Encode(ext, info), outPath];
            await RunAsync(args, len, sub, ct).ConfigureAwait(false);
            outputs.Add(outPath);
        }
        return [dir];
    }

    public static async Task<IReadOnlyList<TimeSpan>> EveryAsync(string input, TimeSpan interval, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var list = new List<TimeSpan>();
        if (interval <= TimeSpan.Zero) return list;
        for (var t = interval; t < info.Duration; t += interval) list.Add(t);
        return list;
    }

    public static async Task<string> JoinAsync(IReadOnlyList<string> inputs, IProgress<double>? progress, CancellationToken ct)
    {
        if (inputs.Count < 2) throw new InvalidOperationException(T("Selecione pelo menos dois arquivos."));
        var infos = new List<MediaInfo>();
        foreach (var f in inputs) infos.Add(await ProbeAsync(f, ct).ConfigureAwait(false));
        var total = TimeSpan.FromTicks(infos.Sum(i => i.Duration.Ticks));
        var video = infos.Any(i => i.HasVideo);

        var args = new List<string>();
        foreach (var f in inputs) args.AddRange(["-i", f]);
        var filter = new System.Text.StringBuilder();
        var n = inputs.Count;

        string outPath;
        if (video)
        {
            var first = infos.First(i => i.HasVideo);
            int W = first.Width / 2 * 2, H = first.Height / 2 * 2;
            var extraInputs = 0;
            var labels = new System.Text.StringBuilder();
            for (var i = 0; i < n; i++)
            {
                filter.Append($"[{i}:v:0]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}];");
                if (infos[i].HasAudio)
                {
                    filter.Append($"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
                    labels.Append($"[v{i}][a{i}]");
                }
                else
                {
                    var idx = n + extraInputs++;
                    args.AddRange(["-f", "lavfi", "-t", Sec(infos[i].Duration.TotalSeconds), "-i", "anullsrc=r=48000:cl=stereo"]);
                    labels.Append($"[v{i}][{idx}:a]");
                }
            }
            filter.Append($"{labels}concat=n={n}:v=1:a=1[v][a]");
            outPath = OutputPaths.Unique(OutputPaths.DirFor(inputs[0]), $"{OutputPaths.StemOf(inputs[0])} - {T("juntos")}", "mp4");
            args.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]",
                "-c:v", "libx264", "-preset", "medium", "-crf", "22", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outPath]);
        }
        else
        {
            var labels = new System.Text.StringBuilder();
            for (var i = 0; i < n; i++)
            {
                filter.Append($"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
                labels.Append($"[a{i}]");
            }
            filter.Append($"{labels}concat=n={n}:v=0:a=1[a]");
            var ext = KeepAudioExt(inputs[0]);
            outPath = OutputPaths.Unique(OutputPaths.DirFor(inputs[0]), $"{OutputPaths.StemOf(inputs[0])} - {T("juntos")}", ext);
            args.AddRange(["-filter_complex", filter.ToString(), "-map", "[a]", .. AudioCodec(ext), outPath]);
        }
        await RunAsync(args, total, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    /// <summary>atempo only accepts 0.5–2.0 on older builds, so chain it.</summary>
    private static IEnumerable<string> Atempo(double factor)
    {
        var parts = new List<string>();
        while (factor > 2.0) { parts.Add("atempo=2.0"); factor /= 2.0; }
        while (factor < 0.5) { parts.Add("atempo=0.5"); factor /= 0.5; }
        parts.Add($"atempo={factor.ToString("0.####", CultureInfo.InvariantCulture)}");
        return parts;
    }

    public static async Task<string> SpeedAsync(string input, double factor, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var ext = KeepExt(input, info);
        var label = factor.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        var outPath = OutputPaths.For(input, ext, label);
        var vf = info.HasVideo ? new[] { $"setpts=PTS/{factor.ToString("0.####", CultureInfo.InvariantCulture)}" } : Array.Empty<string>();
        List<string> args = ["-i", input, .. Encode(ext, info, vf, Atempo(factor)), outPath];
        await RunAsync(args, TimeSpan.FromSeconds(info.Duration.TotalSeconds / factor), progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<IReadOnlyList<string>> SnapshotsAsync(string input, IReadOnlyList<TimeSpan> times, IProgress<double>? progress, CancellationToken ct)
    {
        var outputs = new List<string>();
        var dir = times.Count > 1 ? OutputPaths.FolderFor(input, "quadros") : OutputPaths.DirFor(input);
        var stem = OutputPaths.StemOf(input);
        for (var i = 0; i < times.Count; i++)
        {
            var t = times[i];
            var name = $"{stem} - {t:hh\\-mm\\-ss\\.ff}";
            var outPath = OutputPaths.Unique(dir, name, "png");
            await ProcessRunner.RunAsync(Ff, ["-hide_banner", "-y", "-nostdin", "-ss", Sec(t.TotalSeconds), "-i", input, "-frames:v", "1", outPath], ct).ConfigureAwait(false);
            outputs.Add(outPath);
            progress?.Report((i + 1) / (double)times.Count);
        }
        return times.Count > 1 ? [dir] : outputs;
    }

    /// <summary>A small frame for previews. Returns PNG bytes.</summary>
    public static async Task<byte[]> FrameAsync(string input, TimeSpan t, int maxWidth, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wq-frame-{Guid.NewGuid():N}.png");
        try
        {
            await ProcessRunner.RunAsync(Ff, ["-hide_banner", "-y", "-nostdin", "-ss", Sec(t.TotalSeconds), "-i", input, "-frames:v", "1", "-vf", $"scale='min({maxWidth},iw)':-2", tmp], ct).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
        }
        finally
        {
            OutputPaths.TryDelete(tmp);
        }
    }

    public static async Task<byte[]> WaveformAsync(string input, int width, int height, string color, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wq-wave-{Guid.NewGuid():N}.png");
        try
        {
            await ProcessRunner.RunAsync(Ff, ["-hide_banner", "-y", "-nostdin", "-i", input, "-filter_complex", $"[0:a:0]aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors={color}", "-frames:v", "1", tmp], ct).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
        }
        finally
        {
            OutputPaths.TryDelete(tmp);
        }
    }

    public static async Task<string> CropAspectAsync(string input, string aspect, IProgress<double>? progress, CancellationToken ct)
    {
        var r = ImageEngine.ParseAspect(aspect);
        if (r <= 0) throw new ArgumentException(T("Proporção inválida."));
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var rs = r.ToString("0.######", CultureInfo.InvariantCulture);
        var crop = $"crop='trunc(if(gt(iw/ih,{rs}),ih*{rs},iw)/2)*2':'trunc(if(gt(iw/ih,{rs}),ih,iw/{rs})/2)*2'";
        var ext = KeepVideoExt(input);
        var outPath = OutputPaths.For(input, ext, "recortado");
        List<string> args = ["-i", input, .. Encode(ext, info, [crop]), outPath];
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> RotateVideoAsync(string input, RotateOp op, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var filter = op switch
        {
            RotateOp.Clockwise90 => "transpose=1",
            RotateOp.CounterClockwise90 => "transpose=2",
            RotateOp.Rotate180 => "hflip,vflip",
            RotateOp.FlipHorizontal => "hflip",
            _ => "vflip",
        };
        var ext = KeepVideoExt(input);
        var outPath = OutputPaths.For(input, ext, "girado");
        List<string> args = ["-i", input, .. Encode(ext, info, [filter]), outPath];
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> StripMetadataAsync(string input, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var outPath = OutputPaths.For(input, Formats.Canonical(Formats.Ext(input)), "sem metadados");
        await RunAsync(["-i", input, "-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy", outPath], info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    // ---------- audio tools ----------

    public static async Task<string> CompressAudioAsync(string input, int bitrateKbps, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var ext = KeepAudioExt(input);
        if (ext is "wav" or "flac" or "aiff") ext = "mp3"; // lossless formats can't shrink by bitrate
        if (info.HasVideo && !IsAudioExt(Formats.Ext(input))) return await CompressVideoAsync(input, CompressLevel.Medium, 0, progress, ct).ConfigureAwait(false);
        var outPath = OutputPaths.For(input, ext, "comprimido");
        List<string> args = ["-i", input, .. Encode(ext, info, audioBitrate: bitrateKbps), outPath];
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> NormalizeAsync(string input, double targetLufs, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var ext = KeepExt(input, info);
        var outPath = OutputPaths.For(input, ext, "normalizado");
        var af = $"loudnorm=I={targetLufs.ToString("0.#", CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11";
        List<string> args = ["-i", input];
        if (info.HasVideo)
        {
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0", "-c:v", "copy", "-af", af, .. AudioCodec(ext)]);
        }
        else
        {
            args.AddRange(Encode(ext, info, af: [af, "aresample=48000"]));
        }
        args.Add(outPath);
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> ChannelsAsync(string input, int channels, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var ext = KeepExt(input, info);
        var outPath = OutputPaths.For(input, ext, channels == 1 ? "mono" : "estéreo");
        List<string> args = ["-i", input, .. Encode(ext, info), "-ac", channels.ToString(), outPath];
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> VisualizeAsync(string input, VisualizeShape shape, string color, string background, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var (w, h) = shape switch
        {
            VisualizeShape.Portrait => (1080, 1920),
            VisualizeShape.Square => (1080, 1080),
            _ => (1920, 1080),
        };
        var waveH = h / 3;
        var outPath = OutputPaths.For(input, "mp4", "visual");
        var filter = $"color=c={background}:s={w}x{h}:r=30[bg];" +
                     $"[0:a:0]aformat=channel_layouts=mono,showwaves=s={w}x{waveH}:mode=cline:rate=30:colors={color}[wave];" +
                     $"[bg][wave]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]";
        List<string> args = ["-i", input, "-filter_complex", filter, "-map", "[v]", "-map", "0:a:0",
            "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", outPath];
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> BleepAsync(string input, IReadOnlyList<(TimeSpan Start, TimeSpan End)> ranges, IProgress<double>? progress, CancellationToken ct)
    {
        if (ranges.Count == 0) throw new InvalidOperationException(T("Informe pelo menos um trecho."));
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var expr = string.Join("+", ranges.Select(r => $"between(t,{Sec(r.Start.TotalSeconds)},{Sec(r.End.TotalSeconds)})"));
        var ext = KeepExt(input, info);
        var outPath = OutputPaths.For(input, ext, "bipe");
        var filter = $"[0:a:0]volume=0:enable='{expr}'[m];" +
                     $"[1:a]volume=0.25,volume=0:enable='not({expr})'[b];" +
                     "[m][b]amix=inputs=2:duration=first:normalize=0[a]";
        List<string> args = ["-i", input, "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-filter_complex", filter];
        if (info.HasVideo && !IsAudioExt(ext))
        {
            args.AddRange(["-map", "0:v:0", "-map", "[a]", "-c:v", "copy", .. AudioCodec(ext)]);
        }
        else
        {
            args.AddRange(["-map", "[a]", .. AudioCodec(ext)]);
        }
        args.Add(outPath);
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    public static async Task<string> SetTagsAsync(string input, IReadOnlyDictionary<string, string> tags, bool clearOthers, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(input, ct).ConfigureAwait(false);
        var outPath = OutputPaths.For(input, Formats.Canonical(Formats.Ext(input)), "tags");
        List<string> args = ["-i", input, "-map", "0", "-c", "copy"];
        if (clearOthers) args.AddRange(["-map_metadata", "-1"]);
        foreach (var (k, v) in tags) args.AddRange(["-metadata", $"{k}={v}"]);
        if (Formats.Canonical(Formats.Ext(input)) == "mp3") args.AddRange(["-id3v2_version", "3"]);
        args.Add(outPath);
        await RunAsync(args, info.Duration, progress, ct).ConfigureAwait(false);
        return outPath;
    }

    // ---------- time parsing ----------

    /// <summary>Parses "90", "1:30", "01:02:03.5" or "1m30s" into a time span.</summary>
    public static bool TryParseTime(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        text = text.Trim();
        if (text.Length == 0) return false;
        var m = Regex.Match(text, @"^(?:(\d+)h)?\s*(?:(\d+)m)?\s*(?:(\d+(?:\.\d+)?)s)?$");
        if (m.Success && (m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success))
        {
            value = TimeSpan.FromHours(m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0)
                + TimeSpan.FromMinutes(m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0)
                + TimeSpan.FromSeconds(m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
            return true;
        }
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return false;
        double seconds = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0) return false;
            seconds = seconds * 60 + n;
        }
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    public static IReadOnlyList<TimeSpan> ParseTimes(string text)
    {
        var list = new List<TimeSpan>();
        foreach (var part in text.Split([';', ',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseTime(part, out var t)) list.Add(t);
            else throw new FormatException(T("Tempo inválido: \"{0}\"", part));
        }
        return list;
    }

    public static IReadOnlyList<(TimeSpan Start, TimeSpan End)> ParseRanges(string text)
    {
        var list = new List<(TimeSpan, TimeSpan)>();
        foreach (var part in text.Split([';', ',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var se = part.Split('-');
            if (se.Length != 2 || !TryParseTime(se[0], out var s) || !TryParseTime(se[1], out var e) || e <= s)
            {
                throw new FormatException(T("Trecho inválido: \"{0}\" (use início-fim, ex: 0:10-0:12)", part));
            }
            list.Add((s, e));
        }
        return list;
    }

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
}
