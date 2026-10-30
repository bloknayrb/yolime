using Yolime.Core.Modes;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Llm;

/// <param name="Profile">The target app's style; sets tone and section names in the prompt.</param>
public sealed record RewriteRequest(Mode Mode, string Transcript, CapturedContext? Context, Profile Profile = Profile.Neutral);

/// <remarks>
/// <see cref="Config"/> is a request the API rejects as invalid (400/404/413: check settings); <see cref="Unavailable"/>
/// means no rewriter is configured at all; <see cref="MissingKey"/> fails before any network call.
/// </remarks>
public enum RewriteFailure { Refusal, MaxTokens, Auth, RateLimited, Timeout, Network, Server, Unavailable, Config, MissingKey, EmptyOutput, Billing }

public enum RewriterReadiness { Ready, MissingKey }

/// <summary>Thrown by an <see cref="IRewriter"/>, possibly after some deltas were already yielded.</summary>
public sealed class RewriteException(RewriteFailure failure) : Exception(failure.ToString())
{
    public RewriteFailure Failure { get; } = failure;
}

/// <summary>Turns a transcript into the mode's output, streamed so the overlay can preview it (PR3: Claude).</summary>
public interface IRewriter
{
    /// <summary>Cheap preflight with no network call, like STT's: shown in the tooltip and checked before a retry.</summary>
    RewriterReadiness CheckReady();

    IAsyncEnumerable<string> StreamAsync(RewriteRequest request, CancellationToken ct);
}
