using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinKame.Core.Engines;

public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

public static partial class SubtitleEngine
{
    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[,.](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[,.](\d{1,3})")]
    private static partial Regex TimeRx();

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex TagRx();

    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = text.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Where(l => l.Length > 0).ToList();
            var idx = lines.FindIndex(l => l.Contains("-->"));
            if (idx < 0) continue;
            var m = TimeRx().Match(lines[idx]);
            if (!m.Success) continue;
            var start = Time(m, 1);
            var end = Time(m, 5);
            var body = string.Join("\n", lines.Skip(idx + 1));
            cues.Add(new Cue(start, end, body));
        }
        return cues;
    }

    private static TimeSpan Time(Match m, int g)
    {
        var h = m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        var min = int.Parse(m.Groups[g + 1].Value);
        var s = int.Parse(m.Groups[g + 2].Value);
        var msText = m.Groups[g + 3].Value.PadRight(3, '0');
        return new TimeSpan(0, h, min, s, int.Parse(msText));
    }

    private static string Stamp(TimeSpan t, char msSep) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{msSep}{t.Milliseconds:000}";

    public static string ToSrt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var c in cues)
        {
            sb.Append(i++).Append("\r\n");
            sb.Append(Stamp(c.Start, ',')).Append(" --> ").Append(Stamp(c.End, ',')).Append("\r\n");
            sb.Append(c.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(Stamp(c.Start, '.')).Append(" --> ").Append(Stamp(c.End, '.')).Append('\n');
            sb.Append(c.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToText(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        string? last = null;
        foreach (var c in cues)
        {
            var t = TagRx().Replace(c.Text, "").Replace("\n", " ").Trim();
            if (t.Length == 0 || t == last) continue;
            sb.AppendLine(t);
            last = t;
        }
        return sb.ToString();
    }

    public static string Convert(string input, string target)
    {
        target = Formats.Canonical(target);
        var cues = Parse(File.ReadAllText(input, Encoding.UTF8));
        if (cues.Count == 0) throw new InvalidDataException(T("Nenhuma legenda encontrada no arquivo."));
        var outPath = OutputPaths.For(input, target);
        var content = target switch
        {
            "srt" => ToSrt(cues),
            "vtt" => ToVtt(cues),
            "txt" => ToText(cues),
            _ => throw new NotSupportedException(target),
        };
        File.WriteAllText(outPath, content, new UTF8Encoding(false));
        return outPath;
    }

    public static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}
