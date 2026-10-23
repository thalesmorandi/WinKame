// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WinKame.Core;

namespace WinKame.Jobs;

public enum JobState { Running, Done, Failed, Canceled }

/// <summary>A background task shown in the progress toast.</summary>
public sealed class Job : INotifyPropertyChanged
{
    private string _detail = "";
    private double _progress = -1;
    private JobState _state = JobState.Running;
    private string? _error;

    public string Title { get; init; } = "";
    public CancellationTokenSource Cts { get; } = new();
    public List<string> Outputs { get; } = [];
    public ExternalTool? MissingTool { get; set; }
    public DateTime Finished { get; private set; }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>0..1, or negative while unknown.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.002) return;
            Set(ref _progress, value);
            OnChanged(nameof(Indeterminate));
            OnChanged(nameof(Percent));
        }
    }

    public bool Indeterminate => _progress < 0;
    public double Percent => Math.Max(0, _progress) * 100;

    public JobState State
    {
        get => _state;
        set
        {
            Set(ref _state, value);
            if (value != JobState.Running) Finished = DateTime.Now;
            OnChanged(nameof(IsRunning));
            OnChanged(nameof(IsFinished));
            OnChanged(nameof(CanReveal));
            OnChanged(nameof(StatusGlyph));
            OnChanged(nameof(CanInstall));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            Set(ref _error, value);
            OnChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);
    public bool IsRunning => _state == JobState.Running;
    public bool IsFinished => _state != JobState.Running;
    public bool CanReveal => _state != JobState.Running && Outputs.Count > 0;
    public bool CanInstall => _state == JobState.Failed && MissingTool is not null;
    public string InstallLabel => MissingTool is { } t ? T("Instalar {0}", ToolLocator.DisplayName(t)) : "";

    public string StatusGlyph => _state switch
    {
        JobState.Done => "",
        JobState.Failed => "",
        JobState.Canceled => "",
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name!);
    }

    internal void OnChanged(string name) =>
        Ui.OnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
}

public sealed class JobContext(Job job)
{
    public Job Job { get; } = job;
    public CancellationToken Token => Job.Cts.Token;

    public void Report(double progress) => Ui.OnUi(() => Job.Progress = progress);

    public void Detail(string text) => Ui.OnUi(() => Job.Detail = text);

    public IProgress<double> Progress(double from = 0, double to = 1) =>
        new Progress<double>(v => Job.Progress = from + (to - from) * Math.Clamp(v, 0, 1));

    public void Output(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        Ui.OnUi(() => Job.Outputs.AddRange(list));
    }
}

public static class JobRunner
{
    public static ObservableCollection<Job> All { get; } = [];
    private const int MaxShown = 5;

    public static event Action<Job>? Started;
    public static event Action<Job>? Completed;

    /// <summary>When true, Explorer opens on the results after each job.</summary>
    public static bool RevealWhenDone { get; set; }

    public static Job Start(string title, Func<JobContext, Task> work)
    {
        var job = new Job { Title = T(title) };
        var ctx = new JobContext(job);
        // Keep the jobs card short: drop the oldest finished jobs first.
        while (All.Count >= MaxShown && All.LastOrDefault(j => j.IsFinished) is { } old) All.Remove(old);
        All.Insert(0, job);
        Started?.Invoke(job);

        _ = Task.Run(async () =>
        {
            try
            {
                await work(ctx).ConfigureAwait(false);
                Ui.OnUi(() =>
                {
                    job.Progress = 1;
                    if (job.State == JobState.Running) job.State = JobState.Done;
                    if (string.IsNullOrEmpty(job.Detail)) job.Detail = Summary(job);
                });
            }
            catch (OperationCanceledException)
            {
                Ui.OnUi(() => { job.State = JobState.Canceled; job.Detail = T("Cancelado"); });
            }
            catch (Exception ex)
            {
                var root = Root(ex);
                Ui.OnUi(() =>
                {
                    job.MissingTool = (root as ToolMissingException)?.Tool;
                    job.Error = root.Message;
                    job.Detail = job.Outputs.Count > 0 ? Summary(job) + " · " + T("com erros") : T("Falhou");
                    job.State = JobState.Failed;
                    job.OnChanged(nameof(Job.InstallLabel));
                });
            }
            finally
            {
                Ui.OnUi(() =>
                {
                    Completed?.Invoke(job);
                    if (RevealWhenDone && job.Outputs.Count > 0) Shell.FileOps.Reveal(job.Outputs);
                });
            }
        });
        return job;
    }

    /// <summary>Runs <paramref name="work"/> for each file in turn. Failures don't stop the rest.</summary>
    public static Job ForEach(string title, IReadOnlyList<string> files, Func<string, JobContext, IProgress<double>, Task<IEnumerable<string>>> work)
    {
        return Start(title, async ctx =>
        {
            var errors = new List<string>();
            ToolMissingException? missing = null;
            for (var i = 0; i < files.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var file = files[i];
                ctx.Detail(files.Count > 1 ? T("{0} de {1}", i + 1, files.Count) + " · " + Path.GetFileName(file) : Path.GetFileName(file));
                var index = i;
                var sub = new Progress<double>(v => ctx.Job.Progress = (index + Math.Clamp(v, 0, 1)) / files.Count);
                ctx.Report(i / (double)files.Count);
                try
                {
                    var outs = await work(file, ctx, sub).ConfigureAwait(false);
                    ctx.Output(outs);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ToolMissingException tm)
                {
                    missing = tm;
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(file)}: {Root(ex).Message}");
                }
            }
            if (missing is not null) throw missing;
            if (errors.Count > 0) throw new AggregateFailure(errors);
            ctx.Detail("");
        });
    }

    private static string Summary(Job job)
    {
        if (job.Outputs.Count == 0) return T("Concluído");
        if (job.Outputs.Count == 1) return Path.GetFileName(job.Outputs[0].TrimEnd('\\'));
        return T("{0} itens criados", job.Outputs.Count);
    }

    private static Exception Root(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } ae) ex = ae.InnerException!;
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex;
    }

    public static void ClearFinished()
    {
        foreach (var j in All.Where(j => j.IsFinished).ToList()) All.Remove(j);
    }
}

public sealed class AggregateFailure(IReadOnlyList<string> errors)
    : Exception(errors.Count == 1 ? errors[0] : T("{0} arquivos falharam:", errors.Count) + "\n" + string.Join("\n", errors.Take(6)));
