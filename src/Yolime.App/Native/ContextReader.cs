using System.Collections.Concurrent;
using FlaUI.UIA3;
using Yolime.Core.Capture;
using Yolime.Core.Clipboard;
using Yolime.Core.Logging;
using Yolime.Core.Pipeline;

namespace Yolime.App.Native;

/// <summary>
/// Reply and Prompt context, read without sending any keys to the target: the focused element's TextPattern
/// selection (never its value, name or whole document), else the clipboard if the user copied it within two minutes.
/// UIA runs on its own MTA thread with a 300 ms budget, replaced if a provider hangs.
/// </summary>
internal sealed class ContextReader(ClipboardGuard clipboard, ClipboardFreshness freshness, ILog log) : IContextReader, IDisposable
{
    private static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>A little over the prompt's cap, so the builder can tell that it truncated.</summary>
    private const int MaxChars = Yolime.Core.Llm.PromptBuilder.ContextCap + 1;

    private Worker _worker = NewWarmWorker();
    private Task _lastJob = Task.CompletedTask;
    private long _lastJobStartedMs;
    private const long HungAfterMs = 2000;

    /// <summary>The first UIA query into a process is the slow one: warm the client before the first hold needs it.</summary>
    private static Worker NewWarmWorker()
    {
        var worker = new Worker();
        _ = worker.Run();
        return worker;
    }

    public async Task<CapturedContext?> ReadAsync(CancellationToken ct)
    {
        // Slow is not hung: a job that missed its budget may still finish. Only one still running well past it (a
        // provider that is stuck, not a cold first query) gets the worker replaced.
        if (!_lastJob.IsCompleted && Environment.TickCount64 - _lastJobStartedMs > HungAfterMs)
        {
            _worker.Abandon();
            _worker = NewWarmWorker();
        }
        var job = _worker.Run();
        _lastJob = job;
        _lastJobStartedMs = Environment.TickCount64;
        var finished = await Task.WhenAny(job, Task.Delay(UiaBudget, ct)) == job;
        ct.ThrowIfCancellationRequested();
        if (finished && await job is { Length: > 0 } selected)
            return new CapturedContext(selected.Length > MaxChars ? selected[..MaxChars] : selected, ContextSource.Selection, 0);

        try
        {
            // Checked before the open as well: nothing in yolime may read its own pending paste (an offered copy
            // would be rendered by the read), and only a fresh user copy is ever context.
            if (freshness.UserCopyAgeMs(User32.GetClipboardSequenceNumber()) is null) return null;
            var (text, sequence, sensitive) = await clipboard.ReadTextAsync(ct);
            // A password manager's copy is never context, and a copy the user didn't just make is stale.
            if (sensitive || string.IsNullOrEmpty(text) || freshness.UserCopyAgeMs(sequence) is not { } age) return null;
            return new CapturedContext(text.Length > MaxChars ? text[..MaxChars] : text, ContextSource.Clipboard, age);
        }
        catch (IOException ex)
        {
            log.Write(LogEvent.StageFailed, LogField.Tag("stage", Core.Stage.Rewrite), LogField.Error(ex));
            return null;
        }
    }

    public void Dispose() => _worker.Abandon();

    private sealed class Worker
    {
        private readonly BlockingCollection<TaskCompletionSource<string?>> _jobs = new();

        public Worker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "yolime-uia-selection" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public Task<string?> Run()
        {
            var job = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_jobs.IsAddingCompleted) _jobs.Add(job);
            else job.SetResult(null);
            return job.Task;
        }

        public void Abandon() => _jobs.CompleteAdding();

        private void Loop()
        {
            using var automation = new UIA3Automation { ConnectionTimeout = UiaBudget, TransactionTimeout = UiaBudget };
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                try
                {
                    var el = automation.FocusedElement();
                    if (el.Properties.IsPassword.TryGetValue(out var password) && password)
                    {
                        job.TrySetResult(null);
                        continue;
                    }
                    string? selected = null;
                    if (el.Patterns.Text.TryGetPattern(out var text))
                    {
                        var parts = text.GetSelection().Select(r => r.GetText(MaxChars)).Where(s => !string.IsNullOrEmpty(s));
                        selected = string.Join('\n', parts);
                    }
                    job.TrySetResult(string.IsNullOrWhiteSpace(selected) ? null : selected);
                }
                catch (Exception)
                {
                    job.TrySetResult(null);
                }
            }
        }
    }
}
