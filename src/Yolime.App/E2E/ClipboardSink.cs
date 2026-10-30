#if YOLIME_E2E
using Yolime.App.Native;
using Yolime.Core.History;
using Yolime.Core.Output;

namespace Yolime.App.E2E;

/// <summary>E2E builds only: puts a --test-audio result on the clipboard (synthetic text, kept out of history).</summary>
internal sealed class ClipboardSink : IOutputSink
{
    public async Task<DeliveryKind> DeliverAsync(OutputItem item, CancellationToken ct)
    {
        await new ClipboardGuard(0).SetTextAsync(item.Text, excludeFromHistory: true, ct);
        return DeliveryKind.Copied;
    }
}
#endif
