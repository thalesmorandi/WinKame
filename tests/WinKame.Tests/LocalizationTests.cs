using System.Text.RegularExpressions;
using WinKame.Core;

namespace WinKame.Tests;

internal static class TestLanguage
{
    /// <summary>Tests assert Portuguese file names, so they don't follow the machine's language.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void UsePortuguese() => L.Use("pt");
}

/// <summary>Every interface text in the source has an English translation with the same placeholders.</summary>
public partial class LocalizationTests
{
    private static string Src => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));

    // Output-file suffixes go through OutputPaths as variables, so they are listed here.
    private static readonly string[] Suffixes =
    [
        "comprimido", "redimensionado", "recortado", "girado", "sem metadados", "editado", "fundo", "oculto", "cortado",
        "partes", "quadros", "sem áudio", "normalizado", "mono", "estéreo", "visual", "bipe", "tags", "páginas", "organizado", "saída",
    ];

    // Fragments the scanner picks up from interpolated strings; not interface text.
    private static readonly HashSet<string> NotText = ["{Ui.Plural(files.Count, ", "{action.Name}: {ex.Message}", "{i:000}.png"];

    [GeneratedRegex("""\{l:T '((?:[^'\\]|\\.)*)'\}""")]
    private static partial Regex XamlRx();

    [GeneratedRegex(@"\bT\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CallRx();

    [GeneratedRegex(@"Plural\([^,]+,\s*""((?:[^""\\]|\\.)*)"",\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex PluralRx();

    [GeneratedRegex(@"""((?:[^""\\\r\n]|\\.)*)""")]
    private static partial Regex LiteralRx();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholderRx();

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");

    public static SortedSet<string> SourceKeys()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var ext = Path.GetExtension(file);
            if (ext is not ".cs" and not ".xaml") continue;
            var text = File.ReadAllText(file);
            if (ext == ".xaml")
            {
                foreach (Match m in XamlRx().Matches(text)) keys.Add(m.Groups[1].Value.Replace("\\'", "'"));
                continue;
            }
            foreach (Match m in CallRx().Matches(text)) keys.Add(Unescape(m.Groups[1].Value));
            foreach (Match m in PluralRx().Matches(text))
            {
                keys.Add(m.Groups[1].Value);
                keys.Add(m.Groups[2].Value);
            }
            if (!file.Contains($"{Path.DirectorySeparatorChar}Actions{Path.DirectorySeparatorChar}")) continue;
            // Action files pass their texts to translating sinks (forms, jobs, wheel items).
            foreach (Match m in LiteralRx().Matches(text))
            {
                var v = Unescape(m.Groups[1].Value);
                if (Regex.IsMatch(v, "^[a-z0-9_]+$") || Regex.IsMatch(v, @"^[#\\]") || !Regex.IsMatch(v, @"\w")) continue;
                if (!Regex.IsMatch(v, "[A-Za-zÀ-ú]") || v.StartsWith('{') && char.IsDigit(v.ElementAtOrDefault(1))) continue;
                if (Regex.IsMatch(v, "^(original|grid|row|column|convert|tools|actions|custom|title|artist|album|album_artist|genre|date|track|comment)$")) continue;
                keys.Add(v);
            }
        }
        keys.ExceptWith(NotText);
        keys.UnionWith(Suffixes);
        return keys;
    }

    [Fact]
    public void EveryTextHasATranslation()
    {
        var missing = SourceKeys().Where(k => !L.EnglishTable.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing English for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void PlaceholdersMatch()
    {
        // In named placeholders like {ask:Pergunta} the part after ':' is an example and may be translated.
        static IEnumerable<string> Tokens(string s) => PlaceholderRx().Matches(s)
            .Select(m => Regex.Replace(m.Value, @"^\{([a-z]+):.*\}$", "{$1:…}"))
            .Order();
        var wrong = L.EnglishTable
            .Where(p => !Tokens(p.Key).SequenceEqual(Tokens(p.Value)))
            .Select(p => $"{p.Key} → {p.Value}")
            .ToList();
        Assert.True(wrong.Count == 0, "Placeholders differ:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void NoUnusedTranslations()
    {
        var used = SourceKeys();
        var unused = L.EnglishTable.Keys.Where(k => !used.Contains(k)).ToList();
        Assert.True(unused.Count == 0, "Translations no longer used (typo in a key?):\n" + string.Join("\n", unused));
    }

    [Fact]
    public void TranslatesAndFormats()
    {
        // Tests run in parallel, so this checks the table without switching the global language.
        Assert.Equal("Compress", L.EnglishTable["Comprimir"]);
        Assert.Equal("Convert to PNG", string.Format(L.EnglishTable["Converter para {0}"], "PNG"));
        Assert.Equal(L.English ? "Convert to PNG" : "Converter para PNG", L.T("Converter para {0}", "PNG"));
    }
}
