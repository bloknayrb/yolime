#if YOLIME_E2E
namespace Yolime.Core.Stt;

/// <summary>E2E builds only: returns fixed text, so the Sandbox E2E can run with networking off.</summary>
/// <param name="delayMs">Holds the run in "processing" for a while, so a test can act during it.</param>
public sealed class FakeTranscriptionSession(string text, int delayMs = 0) : ITranscriptionSession
{
    public void PushAudio(ReadOnlyMemory<byte> pcm)
    {
    }

    public async Task<SttResult> CompleteAsync(CancellationToken ct)
    {
        if (delayMs > 0) await Task.Delay(delayMs, ct);
        ct.ThrowIfCancellationRequested();
        return new SttResult(text, 0, 0, false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeTranscriptionSessionFactory(string text, int delayMs = 0) : ITranscriptionSessionFactory
{
    public SttProviderKind Provider => SttProviderKind.Custom;

    public SttReadiness CheckReady() => SttReadiness.Ready;

    public ITranscriptionSession Create(SttSessionOptions options) => new FakeTranscriptionSession(text, delayMs);
}
#endif
