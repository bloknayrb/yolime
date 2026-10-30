using System.Text.Json;

namespace Yolime.E2E;

/// <summary>
/// feat/warm-mic, on its own app launch (the main one keeps the cold path covered): "keepMicReady" on, a file "mic" that
/// keeps playing between holds, as a real open mic does.
/// </summary>
internal sealed partial class Scenarios
{
    private const int CmdPause = 2;

    /// <param name="results">This launch's results folder (its run files, and the mic's open/close log).</param>
    public string? KeepMicReady(string results)
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        int Runs() => Directory.Exists(results) ? Directory.GetFiles(results, "run-*.json").Length : 0;
        string? Run(int n, string field)
        {
            // The app may still be writing it: share the file, and read again until it parses.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return JsonDocument.Parse(ReadShared(Path.Combine(results, $"run-{n}.json"))).RootElement.GetProperty(field).GetString();
                }
                catch (Exception) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
        string[] MicLog() => File.Exists(Path.Combine(results, "mic-opens.txt")) ? ReadShared(Path.Combine(results, "mic-opens.txt")).Split('\n') : [];
        int Opens() => MicLog().Count(l => l == "open");

        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => Runs() >= 1, 10_000)) return "the first hold never finished";
        if (Run(1, "micStart") != "Cold") return $"first hold: micStart {Run(1, "micStart")}";

        // The gap: the mic keeps delivering, and none of it may reach the next hold.
        Thread.Sleep(2500);
        form.On(() => form.Box1.Text = "");
        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => Runs() >= 2, 10_000)) return "the second hold never finished";
        if (Run(2, "micStart") != "Warm") return $"second hold: micStart {Run(2, "micStart")}, opens {Opens()}";
        if (Opens() != 1) return $"the mic was opened {Opens()} times for two holds";
        var bytes = long.Parse(Run(2, "audioBytes")!);
        // About 1.5 s of audio; the 2.5 s gap before it would make it 4 s.
        if (bytes > 1900 * 32) return $"the second hold has {bytes / 32} ms of audio: the gap leaked in";
        if (bytes < 1100 * 32) return $"the second hold has only {bytes / 32} ms of audio";

        // Pause hotkeys closes the mic at once; the next hold opens it again.
        if (TrayCommand(CmdPause) is { } pauseFail) return pauseFail;
        if (!Program.Wait(() => MicLog().Contains("close"), 3000)) return "Pause hotkeys didn't close the mic";
        TrayCommand(CmdPause);
        Thread.Sleep(500);
        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => Runs() >= 3, 10_000)) return "the hold after the pause never finished";
        return Opens() == 2 && Run(3, "micStart") == "Cold" ? null : $"after the pause: opens {Opens()}, micStart {Run(3, "micStart")}";
    }

    private static string ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f);
        return r.ReadToEnd();
    }
}
