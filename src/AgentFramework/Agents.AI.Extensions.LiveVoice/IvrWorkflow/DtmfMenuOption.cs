using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Describes the action to take when a specific DTMF digit is pressed inside a menu step.
/// Combines an existing <see cref="AITool"/> (typically one already exposed to the LLM,
/// such as <c>transfer_call</c> or <c>hang_up_call</c>) with arguments bound at
/// configuration time, an optional declarative transition, and an optional failure prompt.
/// </summary>
/// <remarks>
/// When <see cref="Action"/> is <see langword="null"/>, the option is purely declarative:
/// pressing the digit moves the workflow to <see cref="NextStepId"/> (or the legacy
/// "label as step id" behaviour if both are null).
///
/// When <see cref="Action"/> is set, the strategy invokes the tool with <see cref="Arguments"/>
/// (resolved through the call-scoped <see cref="IServiceProvider"/>), then interprets the
/// return value to decide what to do next. See <see cref="DtmfActionResult"/> for the
/// supported return shapes.
/// </remarks>
public sealed record DtmfMenuOption
{
    /// <summary>The DTMF digit ('0'-'9', '*', '#') that selects this option.</summary>
    public required char Digit { get; init; }

    /// <summary>Human-readable label spoken in the menu prompt (e.g. "Billing").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Tool invoked when the digit is pressed. Optional — when null, the option is
    /// purely declarative and transitions to <see cref="NextStepId"/>.
    /// </summary>
    public AITool? Action { get; init; }

    /// <summary>
    /// Arguments bound at configuration time. The strategy passes these to
    /// <see cref="AIFunction.InvokeAsync"/> alongside the call-scoped service provider.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>
    /// Step to transition to after the action succeeds. When the action returns an
    /// explicit <see cref="DtmfActionResult"/>, that result wins and this value is ignored.
    /// When <see langword="null"/> and the action succeeds, the strategy stays on the
    /// current step (useful for actions that hang up or transfer the call externally).
    /// </summary>
    public string? NextStepId { get; init; }

    /// <summary>Prompt spoken when the action signals failure (or throws).</summary>
    public string? OnFailurePrompt { get; init; }

    /// <summary>Audio file played when the action signals failure (alternative to <see cref="OnFailurePrompt"/>).</summary>
    public Uri? OnFailureAudioFile { get; init; }
}
