namespace Yolime.E2E;

/// <summary>fix/preview-gesture: Shift held at release previews this mode; a Shift tap that rolls over it still cycles.</summary>
internal sealed partial class Scenarios
{
    public IEnumerable<Scenario> PreviewGesture() =>
    [
        new("PreviewByShift_KeepsTheMode", PreviewByShift),
        new("ShiftTapRolledOverRelease_Cycles", ShiftTapRolledOver),
    ];

    /// <summary>
    /// Dictate (which doesn't preview on its own), Shift held 400 ms through the release: a Dictate preview. Enter is
    /// pressed while Shift is still down, the way people do, and must still insert (a swallowed Shift never counts as a
    /// modifier held for the paste).
    /// </summary>
    private string? PreviewByShift()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        var previews = Count("Preview");
        Input.Down(Input.RCtrl);
        Thread.Sleep(600);
        Input.Down(Input.LShift);
        Thread.Sleep(400);
        Input.Up(Input.RCtrl);
        if (!Program.Wait(() => Count("Preview") > previews, 6000)) { Input.Up(Input.LShift); return "the preview card never showed"; }
        Thread.Sleep(300);
        Input.Tap(Input.Enter);
        Thread.Sleep(500);
        Input.Up(Input.LShift);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("mode") != "Dictate") return $"mode {LastRun("mode")}: the Shift cycled instead of previewing";
        return Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 6000) ? null : $"box1 is '{form.On(() => form.Box1.Text)}'";
    }

    /// <summary>Shift down, the hold key up 40 ms later, Shift up 30 ms after that: a mode tap, so Prompt and no card.</summary>
    private string? ShiftTapRolledOver()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        var previews = Count("Preview");
        Input.Down(Input.RCtrl);
        Thread.Sleep(600);
        Input.Down(Input.LShift);
        Thread.Sleep(40);
        Input.Up(Input.RCtrl);
        Thread.Sleep(30);
        Input.Up(Input.LShift);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("mode") != "Prompt") return $"mode {LastRun("mode")}: the tap didn't cycle";
        if (Count("Preview") > previews) return "a preview card showed";
        return LastRun("delivery") == "Pasted" ? null : $"delivery {LastRun("delivery")}";
    }
}
