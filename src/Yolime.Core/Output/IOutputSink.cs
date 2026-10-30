using Yolime.Core.History;

namespace Yolime.Core.Output;

public enum DeliveryIntent { Paste, ClipboardOnly }

/// <param name="Mode">The run's final mode; terminals treat Reply differently.</param>
/// <param name="RunId">Lets the sink find the target snapshot taken when this run started.</param>
/// <param name="QuietCopy">The run will explain itself (a rewrite failure, a held-back reply), so a copy shows no notice of its own.</param>
public sealed record OutputItem(string Text, DeliveryIntent Intent, bool PreviewRequested, Mode Mode, Guid RunId, bool QuietCopy = false);

/// <summary>
/// Puts the result where it belongs: pasted at the cursor, or left on the clipboard. The sink owns all output
/// timing, including waits on the user (preview card, "click where the reply should go"), so the pipeline never
/// times it out.
/// </summary>
public interface IOutputSink
{
    Task<DeliveryKind> DeliverAsync(OutputItem item, CancellationToken ct);
}
