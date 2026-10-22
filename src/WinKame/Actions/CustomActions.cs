using WinKame.Core;
using WinKame.Dialogs;
using WinKame.Jobs;
using WinKame.Wheel;

namespace WinKame.Actions;

/// <summary>Turns the user's custom actions into wheel slices.</summary>
internal static class CustomActions
{
    public static string WheelKey(WheelKind k) => k switch
    {
        WheelKind.Convert => "convert",
        WheelKind.Tools => "tools",
        WheelKind.Actions => "actions",
        _ => "custom",
    };

    public static IEnumerable<WheelItem> ItemsFor(WheelKind kind, IReadOnlyList<string> files, AppSettings settings)
    {
        var key = WheelKey(kind);
        foreach (var a in settings.CustomActions)
        {
            if (!string.Equals(a.Wheel, key, StringComparison.OrdinalIgnoreCase) || !a.Matches(files)) continue;
            var action = a.Clone();
            var glyph = string.IsNullOrWhiteSpace(action.Glyph) ? "" : action.Glyph;
            yield return new WheelItem(action.Name, glyph, f => RunAsync(action, f), action.Command);
        }
    }

    public static Task RunAsync(CustomAction action, IReadOnlyList<string> files)
    {
        var answers = new Dictionary<string, string>();
        var questions = CustomActionRunner.Questions(action);
        if (questions.Count > 0)
        {
            var v = FormDialog.Show(action.Name, Ui.Plural(files.Count, "arquivo", "arquivos"),
                questions.Select((q, i) => (Field)new TextField("q" + i, q)).ToList(), "Executar");
            if (v is null) return Task.CompletedTask;
            for (var i = 0; i < questions.Count; i++) answers[questions[i]] = v.Text("q" + i);
        }

        if (action.WaitForExit)
        {
            JobRunner.Start(action.Name, async ctx =>
            {
                var outs = await CustomActionRunner.RunAsync(action, files, answers, ctx.Progress(), ctx.Token);
                ctx.Output(outs);
            });
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await CustomActionRunner.RunAsync(action, files, answers, null, default);
                }
                catch (Exception ex)
                {
                    Ui.OnUi(() => Ui.Error($"{action.Name}: {ex.Message}"));
                }
            });
        }
        return Task.CompletedTask;
    }
}
