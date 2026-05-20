using System.Collections.Generic;
using Agents.AI.ContactCenter.Configuration;

namespace Agents.AI.ContactCenter.IvrWorkflow.Strategies;

/// <summary>
/// Declarative interaction mode chosen by an IVR workflow author. Mirrors
/// <see cref="AgentTier"/> for the common cases and adds <see cref="Mixed"/> to mean
/// "compose multiple modes side-by-side".
/// </summary>
/// <remarks>
/// <para>
/// <b>Mixed mapping caveat.</b> <see cref="IvrInteractionMode.Mixed"/> currently maps to
/// <see cref="AgentTier.RealtimeVoice"/> via <see cref="IvrInteractionModeMappings.ToTier"/>,
/// and <see cref="Strategies.IvrStrategySelector"/> only yields that single tier for a
/// <c>Mixed</c> primary. In practice that means a workflow declaring <c>primary: mixed</c>
/// behaves identically to <c>primary: realtime</c> with the configured fallback chain.
/// </para>
/// <para>
/// Authors who want realtime + NLU + DTMF composed in parallel should keep
/// <c>primary: realtime</c> and rely on the composite fallback wiring (see
/// <c>CallSessionContainerExtensions.AddCompositeFallbackStrategy</c>). A future
/// enhancement may map <c>Mixed</c> to a dedicated composite factory; until then the
/// current alias keeps the YAML surface forward-compatible.
/// </para>
/// </remarks>
public enum IvrInteractionMode
{
    Realtime,
    ChatCompletion,
    SmallLanguageModel,
    Nlu,
    Dtmf,
    Mixed,
}

/// <summary>
/// Compiled strategy policy attached to <see cref="Compilation.CompiledIvrWorkflow"/>
/// and to each <see cref="Compilation.CompiledIvrStage"/>. The host's session
/// factory inspects <see cref="Primary"/>, <see cref="Fallback"/>, and
/// <see cref="PrewarmTiers"/> when selecting an <c>IConversationStrategyFactory</c>.
/// </summary>
public sealed class IvrStrategyPolicy
{
    public IvrStrategyPolicy(
        IvrInteractionMode primary,
        IReadOnlyList<IvrInteractionMode> fallback,
        IReadOnlyList<IvrInteractionMode> prewarmTiers,
        bool allowMidCallDegradation)
    {
        Primary = primary;
        Fallback = fallback;
        PrewarmTiers = prewarmTiers;
        AllowMidCallDegradation = allowMidCallDegradation;
    }

    public IvrInteractionMode Primary { get; }
    public IReadOnlyList<IvrInteractionMode> Fallback { get; }
    public IReadOnlyList<IvrInteractionMode> PrewarmTiers { get; }
    public bool AllowMidCallDegradation { get; }

    /// <summary>
    /// Default policy when the YAML omits a strategy block: realtime-first, fall back to
    /// NLU then DTMF, prewarm DTMF, mid-call degradation enabled.
    /// </summary>
    public static IvrStrategyPolicy Default { get; } = new(
        IvrInteractionMode.Realtime,
        [ IvrInteractionMode.Nlu, IvrInteractionMode.Dtmf ],
        [ IvrInteractionMode.Dtmf ],
        allowMidCallDegradation: true);
}

/// <summary>Bridges between the declarative <see cref="IvrInteractionMode"/> and runtime <see cref="AgentTier"/>.</summary>
public static class IvrInteractionModeMappings
{
    /// <summary>Map an <see cref="IvrInteractionMode"/> to the matching <see cref="AgentTier"/>. <see cref="IvrInteractionMode.Mixed"/> falls back to <see cref="AgentTier.RealtimeVoice"/>.</summary>
    public static AgentTier ToTier(this IvrInteractionMode mode) => mode switch
    {
        IvrInteractionMode.Realtime => AgentTier.RealtimeVoice,
        IvrInteractionMode.ChatCompletion => AgentTier.ChatCompletionTts,
        IvrInteractionMode.SmallLanguageModel => AgentTier.SmallLanguageModel,
        IvrInteractionMode.Nlu => AgentTier.IntentNlu,
        IvrInteractionMode.Dtmf => AgentTier.DtmfOnly,
        IvrInteractionMode.Mixed => AgentTier.RealtimeVoice,
        _ => AgentTier.DtmfOnly,
    };

    /// <summary>Map an <see cref="AgentTier"/> back to <see cref="IvrInteractionMode"/>.</summary>
    public static IvrInteractionMode ToMode(this AgentTier tier) => tier switch
    {
        AgentTier.RealtimeVoice => IvrInteractionMode.Realtime,
        AgentTier.ChatCompletionTts => IvrInteractionMode.ChatCompletion,
        AgentTier.SmallLanguageModel => IvrInteractionMode.SmallLanguageModel,
        AgentTier.IntentNlu => IvrInteractionMode.Nlu,
        AgentTier.DtmfOnly => IvrInteractionMode.Dtmf,
        _ => IvrInteractionMode.Dtmf,
    };

    /// <summary>Parse a YAML mode string, accepting common synonyms. Returns <see langword="null"/> when unrecognized.</summary>
    public static IvrInteractionMode? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return value.Trim().ToLowerInvariant() switch
        {
            "realtime" or "realtimevoice" or "voice" => IvrInteractionMode.Realtime,
            "chat" or "chatcompletion" or "stt-tts" or "stttts" => IvrInteractionMode.ChatCompletion,
            "slm" or "smalllanguagemodel" => IvrInteractionMode.SmallLanguageModel,
            "nlu" or "intent" or "intentnlu" => IvrInteractionMode.Nlu,
            "dtmf" or "dtmfonly" => IvrInteractionMode.Dtmf,
            "mixed" => IvrInteractionMode.Mixed,
            _ => null,
        };
    }
}
