using System;
using System.Collections.Generic;
using Microsoft.Agents.AI;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// Configuration for <see cref="IvrIntentAgent"/>.
/// </summary>
public sealed class IvrIntentAgentOptions
{
    /// <summary>Optional stable identifier exposed via <see cref="AIAgent.Id"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Display name exposed via <see cref="AIAgent.Name"/>. Defaults to <c>IntentAgent</c>.</summary>
    public string Name { get; set; } = "IntentAgent";

    /// <summary>Free-form description exposed via <see cref="AIAgent.Description"/>.</summary>
    public string? Description { get; set; }
        = "Routes caller utterances to IVR intents via an IChatClient-backed classifier.";

    /// <summary>
    /// Default candidate intents used when a caller invokes <see cref="AIAgent.RunAsync"/>
    /// without supplying an <see cref="IvrIntentRunOptions"/> override. Optional.
    /// </summary>
    public IReadOnlyList<string>? DefaultIntents { get; set; }
}

/// <summary>
/// Per-run override for <see cref="IvrIntentAgent"/> that lets callers specify the
/// candidate intent set for a single <see cref="AIAgent.RunAsync"/> invocation.
/// </summary>
public sealed class IvrIntentRunOptions : AgentRunOptions
{
    /// <summary>Candidate intents this run is restricted to.</summary>
    public IReadOnlyList<string>? ValidIntents { get; set; }
}
