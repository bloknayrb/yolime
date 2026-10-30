using Yolime.Core.Llm;
using Yolime.Core.Stt;

namespace Yolime.Core.Keys;

/// <summary>
/// Where saved keys live (Windows Credential Manager in the app). Implementations never throw: a read that fails is
/// "not saved" and a write that fails returns false.
/// </summary>
public interface ICredentialStore
{
    string? Read(string name);
    bool Write(string name, string secret);
    bool Delete(string name);
}

public enum KeyOrigin { None, Saved, Environment }

/// <param name="LastFour">The key's last four characters, only for keys long enough that the tail gives little away.</param>
public sealed record KeyStatus(KeyOrigin Origin, string? LastFour);

public enum KeySaveError { None, Empty, BadCharacters, TooLong, StoreFailed, UnknownSlot }

/// <summary>
/// The one key source for transcription and Claude: a key saved in Settings, else the environment variable of the same
/// name. Only the four slots are ever read, so <c>ANTHROPIC_API_KEY</c> (which may belong to another tool's budget)
/// is never picked up.
/// </summary>
public sealed class KeySource(ICredentialStore store, Func<string, string?> environment) : IApiKeySource
{
    public const int MaxKeyLength = 2560;
    private const int ShowTailFrom = 20;

    public static IReadOnlyList<string> Slots { get; } =
        [SttEndpoint.OpenAiKeyName, SttEndpoint.GroqKeyName, SttEndpoint.CustomKeyName, ClaudeRewriter.KeyName];

    public KeySource(ICredentialStore store)
        : this(store, n => Environment.GetEnvironmentVariable(n))
    {
    }

    /// <summary>The slot name of a key that was saved or removed.</summary>
    public event Action<string>? Changed;

    public string? Get(string name)
    {
        if (!IsSlot(name)) return null;
        return store.Read(name) is { Length: > 0 } saved ? saved
            : environment(name) is { Length: > 0 } env ? env
            : null;
    }

    public KeyStatus Status(string name)
    {
        if (!IsSlot(name)) return new(KeyOrigin.None, null);
        var (origin, key) = store.Read(name) is { Length: > 0 } saved ? (KeyOrigin.Saved, saved)
            : environment(name) is { Length: > 0 } env ? (KeyOrigin.Environment, env)
            : (KeyOrigin.None, null);
        return new(origin, key is { Length: >= ShowTailFrom } ? key[^4..] : null);
    }

    public KeySaveError Save(string name, string raw)
    {
        if (!IsSlot(name)) return KeySaveError.UnknownSlot;
        var key = raw.Trim();
        if (key.Length == 0) return KeySaveError.Empty;
        // Printable ASCII only: a pasted key with a zero-width or accented character would fail at the provider with
        // an error that doesn't say why.
        if (!IsWellFormed(key)) return key.Length > MaxKeyLength ? KeySaveError.TooLong : KeySaveError.BadCharacters;
        if (!store.Write(name, key)) return KeySaveError.StoreFailed;
        Changed?.Invoke(name);
        return KeySaveError.None;
    }

    public bool Remove(string name)
    {
        if (!IsSlot(name)) return false;
        var ok = store.Delete(name);
        Changed?.Invoke(name);
        return ok;
    }

    /// <summary>Printable ASCII with no spaces, and not absurdly long: every provider's keys look like this.</summary>
    public static bool IsWellFormed(string key) =>
        key.Length is > 0 and <= MaxKeyLength && key.All(c => c is >= '!' and <= '~');

    private static bool IsSlot(string name) => Slots.Contains(name, StringComparer.Ordinal);
}
