using Yolime.Core.Output;
using Yolime.Core.Settings;

namespace Yolime.Core.Modes;

public static class ModelIds
{
    public const string Haiku = "claude-haiku-5-5";
    public const string Sonnet = "claude-sonnet-5-5";

    /// <summary>The models a setting may name. Anything else is a typo, reset to the default on load.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { Haiku, Sonnet };
}

public enum Effort { Low, Medium, High }

/// <summary>The request's <c>thinking</c> field. <see cref="Omit"/> leaves it out (the model's adaptive default).</summary>
public enum Thinking { Disabled, BetweenTools, Omit }

/// <param name="FirstTokenTimeout">How long the stream may take to produce its first text before the run falls back.</param>
public sealed record ModeDefinition(
    Mode Mode,
    string Model,
    Effort Effort,
    Thinking Thinking,
    string PromptId,
    bool UsesContext,
    int MaxTokens,
    DeliveryIntent FallbackIntent,
    TimeSpan FirstTokenTimeout);

/// <summary>What each rewriting mode sends to Claude, and what happens when it fails (spec: Rewrite, No lost work).</summary>
public static class ModeCatalog
{
    /// <summary>Null for <see cref="Mode.Raw"/>, which never rewrites.</summary>
    public static ModeDefinition? For(Mode mode, YolimeSettings settings) => mode switch
    {
        Mode.Dictate => Define(mode, ModelIds.Haiku, "dictate", usesContext: false, 2048, DeliveryIntent.Paste, 5),
        Mode.Prompt => Define(mode, settings.PromptModel, "prompt", usesContext: true, 4096, DeliveryIntent.ClipboardOnly, 8),
        Mode.Reply => Define(mode, ModelIds.Sonnet, "reply", usesContext: true, 2048, DeliveryIntent.ClipboardOnly, 8),
        _ => null,
    };

    /// <summary>
    /// Thinking depends on the model, not the mode: Haiku 5.5 accepts "disabled" at effort high or below, Sonnet 5.5
    /// rejects it with a 400 and takes "between_tools" instead, and anything else keeps its adaptive default.
    /// </summary>
    public static Thinking ThinkingFor(string model) => model switch
    {
        ModelIds.Haiku => Thinking.Disabled,
        ModelIds.Sonnet => Thinking.BetweenTools,
        _ => Thinking.Omit,
    };

    private static ModeDefinition Define(Mode mode, string model, string promptId, bool usesContext, int maxTokens, DeliveryIntent fallback, int firstTokenSeconds) =>
        new(mode, model, Effort.Low, ThinkingFor(model), promptId, usesContext, maxTokens, fallback, TimeSpan.FromSeconds(firstTokenSeconds));
}
