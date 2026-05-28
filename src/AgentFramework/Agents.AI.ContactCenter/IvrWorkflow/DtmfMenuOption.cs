namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Describes the action to take when a specific DTMF digit is pressed inside a menu step.
/// References an existing tool by name (resolved from
/// <see cref="RealtimeIvrWorkflowStep.AvailableTools"/>) plus arguments bound at
/// configuration time, an optional declarative transition, and an optional failure prompt.
/// </summary>
/// <remarks>
/// When <see cref="ActionToolName"/> is <see langword="null"/>, the option is purely
/// declarative: pressing the digit moves the workflow to <see cref="NextStepId"/>.
///
/// When <see cref="ActionToolName"/> is set, the strategy looks up the tool in the
/// owning step's <see cref="RealtimeIvrWorkflowStep.AvailableTools"/>, invokes it with
/// <see cref="Arguments"/> (resolved through the call-scoped
/// <see cref="IServiceProvider"/>), then interprets the return value to decide
/// what to do next. See <see cref="DtmfActionResult"/> for the supported return shapes.
/// </remarks>
public sealed record DtmfMenuOption
{
    /// <summary>The DTMF digit ('0'-'9', '*', '#') that selects this option.</summary>
    public required char Digit { get; init; }

    /// <summary>Human-readable label spoken in the menu prompt (e.g. "Billing").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Name of the tool invoked when the digit is pressed. Resolved against
    /// <see cref="RealtimeIvrWorkflowStep.AvailableTools"/> by
    /// <see cref="Microsoft.Extensions.AI.AITool.Name"/>. Optional — when null, the
    /// option is purely declarative and transitions to <see cref="NextStepId"/>.
    /// </summary>
    public string? ActionToolName { get; init; }

    /// <summary>
    /// Arguments bound at configuration time. The strategy passes these to
    /// <see cref="Microsoft.Extensions.AI.AIFunction.InvokeAsync"/> alongside the call-scoped
    /// service provider.
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

    /// <summary>
    /// Phase 3: guards that must pass before this digit's transition fires. Combined with
    /// the target step's stage-level guards at evaluation time. When any guard fails the
    /// navigator looks up a matching auth-resolver from
    /// <see cref="RealtimeIvrWorkflowDefinition.AuthResolvers"/> and detours through the
    /// named sub-workflow before re-applying.
    /// </summary>
    public IReadOnlyList<IIvrStepGuard> Guards { get; init; } = [];
}
