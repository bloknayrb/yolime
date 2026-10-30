namespace Yolime.E2E;

/// <summary>fix/hold-races: another process taking the foreground mid-hold.</summary>
internal sealed partial class Scenarios
{
    public IEnumerable<Scenario> Foreground() =>
    [
        new("ForegroundFlash_KeepsTheHold", ForegroundFlash),
        new("ForegroundSwitch_DiscardsTheHold", ForegroundSwitch),
    ];

    /// <summary>A hidden window in front for 80 ms (a background helper's flash): the hold still pastes.</summary>
    private string? ForegroundFlash()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Down(Input.RCtrl);
        Thread.Sleep(400);
        var flash = Flash.Run(form.On(() => form.Handle), visible: false, holdMs: 80);
        Thread.Sleep(800);
        Input.Up(Input.RCtrl);
        if (flash is not null) return flash;
        if (!WaitRun(runs)) return "no run finished";
        return Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 4000) ? null : $"outcome {LastRun("outcome")}, box1 '{form.On(() => form.Box1.Text)}'";
    }

    /// <summary>A visible window from another app in front for a second: a real switch, so the hold is discarded.</summary>
    /// <remarks>
    /// Checked in the app's log, not its notices: repeats of a notice are rate-limited, so whether "Discarded" shows
    /// depends on the scenarios before this one.
    /// </remarks>
    private string? ForegroundSwitch()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var before = ForegroundDiscards();
        Input.Down(Input.RCtrl);
        Thread.Sleep(600);
        var flash = Flash.Run(form.On(() => form.Handle), visible: true, holdMs: 1000);
        Input.Up(Input.RCtrl);
        if (flash is not null) return flash;
        Thread.Sleep(3000);
        if (form.On(() => form.Box1.Text) != "") return "text was pasted";
        return ForegroundDiscards() > before ? null : $"not discarded for the switch (outcome {LastRun("outcome")})";
    }

    /// <summary>The app's content-free log lines for holds discarded by a foreground switch.</summary>
    private static int ForegroundDiscards()
    {
        const string logs = @"C:\e2edata\local\logs";
        if (!Directory.Exists(logs)) return 0;
        var n = 0;
        foreach (var file in Directory.GetFiles(logs, "yolime-*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (line.Contains("HoldDiscarded reason=ForegroundChanged", StringComparison.Ordinal)) n++;
        }
        return n;
    }
}
