using Yolime.App.Native;
using Yolime.Core.Hotkeys;
using Yolime.Core.Output;
using Yolime.Core.Overlay;

namespace Yolime.App.Shell;

/// <summary>
/// The preview card: arms the hook's capture first (so the card's keys can't leak into the app), then shows the
/// card, then waits for a key, a button, or anything that ends it.
/// </summary>
/// <param name="hooksLater">The hook thread is built after the executor that needs this prompt.</param>
internal sealed class PreviewPrompt(OverlayController overlay, Func<HookThread> hooksLater) : IPreviewPrompt
{
    public async Task<PreviewChoice> AskAsync(string text, CancellationToken ct)
    {
        var hooks = hooksLater();
        var choice = new TaskCompletionSource<PreviewChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(PreviewSignal s)
        {
            switch (s)
            {
                case PreviewSignal.Insert: choice.TrySetResult(PreviewChoice.Insert); break;
                case PreviewSignal.Copy: choice.TrySetResult(PreviewChoice.Copy); break;
                case PreviewSignal.Discard: choice.TrySetResult(PreviewChoice.Discard); break;
                case PreviewSignal.Expired: choice.TrySetResult(PreviewChoice.Expired); break;
                case PreviewSignal.ScrollUp: overlay.PreviewScroll(-1); break;
                case PreviewSignal.ScrollDown: overlay.PreviewScroll(1); break;
                case PreviewSignal.PageUp: overlay.PreviewScroll(-10); break;
                case PreviewSignal.PageDown: overlay.PreviewScroll(10); break;
            }
        }
        void OnButton(PreviewChoice c) => choice.TrySetResult(c);

        hooks.PreviewSignaled += OnSignal;
        overlay.PreviewChosen += OnButton;
        try
        {
            Native.User32.GetWindowThreadProcessId(Native.User32.GetForegroundWindow(), out var pid);
            await hooks.ArmPreviewAsync((int)pid).WaitAsync(ct);
            overlay.Post(new PreviewShown(text));
            using var cancel = ct.Register(() => choice.TrySetCanceled(ct));
            return await choice.Task;
        }
        finally
        {
            hooks.DisarmPreview();
            hooks.PreviewSignaled -= OnSignal;
            overlay.PreviewChosen -= OnButton;
            overlay.Post(new PreviewEnded());
        }
    }
}
