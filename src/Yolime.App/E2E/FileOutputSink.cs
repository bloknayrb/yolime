#if YOLIME_E2E
using Yolime.Core.History;
using Yolime.Core.Output;

namespace Yolime.App.E2E;

/// <summary>E2E builds only: writes the result to a new file instead of the clipboard. Never overwrites.</summary>
internal sealed class FileOutputSink(string path) : IOutputSink
{
    public async Task<DeliveryKind> DeliverAsync(OutputItem item, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(fs);
        await writer.WriteAsync(item.Text.AsMemory(), ct);
        return DeliveryKind.Pasted;
    }
}
#endif
