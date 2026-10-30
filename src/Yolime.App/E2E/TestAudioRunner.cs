#if YOLIME_E2E
using Yolime.App.Native;
using Yolime.Core;
using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Logging;
using Yolime.Core.Net;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Settings;
using Yolime.Core.Startup;
using Yolime.Core.Stt;

namespace Yolime.App.E2E;

/// <summary>
/// E2E builds only. Runs one Raw dictation from a WAV file, without XAML, and exits with a code per outcome:
/// 0 Completed, 2 NoSpeech, 3 SttFailed, 4 OutputFailed, 5 CaptureFailed, 1 anything else.
/// <code>
/// Yolime.App.exe --test-audio x.wav [--test-out out.txt] [--test-data-dir dir] [--test-transcript "text"]
/// </code>
/// Without <c>--test-out</c> the result goes to the real clipboard. Data defaults to a fresh temp folder, never the
/// user's real history.
/// </summary>
internal static class TestAudioRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var wav = Arg(args, "--test-audio") ?? throw new ArgumentException("--test-audio needs a path");
        var dataDir = Arg(args, "--test-data-dir") ?? Path.Combine(Path.GetTempPath(), "yolime-e2e", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(dataDir, "roaming"), Path.Combine(dataDir, "local"));
        var time = TimeProvider.System;
        var log = new FileLog(paths, time);
        Program.InstallCrashLogging(log);

        var settings = new YolimeSettings();
        var pending = new PendingAudioStore(paths, log, time);
        using var history = new HistoryStore(paths, log, time, pending);
        Housekeeping.Run(history, pending, settings);

        ITranscriptionSessionFactory stt = Arg(args, "--test-transcript") is { } fixedText
            ? new FakeTranscriptionSessionFactory(fixedText)
            : new BatchTranscriptionSessionFactory(() => settings, new EnvironmentApiKeySource(), HttpClients.Shared, log);
        IOutputSink sink = Arg(args, "--test-out") is { } outFile ? new FileOutputSink(outFile) : new ClipboardSink();

        var pipeline = new DictationPipeline(stt, null, sink, history, pending, log, new JsonlTimingLog(paths, time), () => settings, time);
        var request = new RunRequest(new FileAudioSource(wav), Mode.Raw, "yolime-e2e", Task.FromResult(new RunIntent(Mode.Raw, false)));
        var result = await pipeline.RunAsync(request, CancellationToken.None);
        return result.Outcome switch
        {
            RunOutcome.Completed => 0,
            RunOutcome.NoSpeech => 2,
            RunOutcome.SttFailed => 3,
            RunOutcome.OutputFailed => 4,
            RunOutcome.CaptureFailed => 5,
            _ => 1,
        };
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
#endif
