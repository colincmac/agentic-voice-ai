using System.Diagnostics.CodeAnalysis;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Per-call workflow navigator. Wraps a <see cref="RealtimeIvrWorkflowDefinition"/>
/// and its mutable <see cref="IvrWorkflowState"/>, providing a single source of truth
/// for step lookup, transitions, DTMF menu resolution, action invocation, and prompt
/// rendering. Strategies own all I/O (audio, events); the navigator owns the state
/// machine.
/// </summary>
public interface IIvrWorkflowNavigator
{
    /// <summary>The workflow definition this navigator drives.</summary>
    RealtimeIvrWorkflowDefinition Definition { get; }

    /// <summary>The mutable state being driven. The navigator is the only mutator.</summary>
    IvrWorkflowState State { get; }

    /// <summary>The step the call is currently on, or <see langword="null"/> before <see cref="EnterInitialStep"/>.</summary>
    RealtimeIvrWorkflowStep? CurrentStep { get; }

    /// <summary>
    /// Enter <see cref="RealtimeIvrWorkflowDefinition.InitialStepId"/> (or resume the
    /// step recorded on <see cref="IvrWorkflowState.CurrentStepName"/> if present —
    /// useful after a tier swap restored from a prior state). Sets
    /// <see cref="IvrWorkflowState.Status"/> to <see cref="IvrWorkflowStatus.Running"/>
    /// and stamps <see cref="IvrWorkflowState.StepStartedAt"/>.
    /// </summary>
    RealtimeIvrWorkflowStep EnterInitialStep();

    /// <summary>
    /// Validate <paramref name="targetStepId"/> against the current step's
    /// <see cref="RealtimeIvrWorkflowStep.ValidTransitions"/>, mark the current step
    /// completed, and advance. Pure state-machine; no I/O.
    /// </summary>
    TransitionResult TransitionTo(string targetStepId);

    /// <summary>
    /// Mark the workflow complete (or failed/cancelled). Marks the current step
    /// completed if one is set. Does not emit audio or events.
    /// </summary>
    void Complete(IvrWorkflowStatus status = IvrWorkflowStatus.Completed);

    /// <summary>
    /// Push a child workflow (resolved by id through the navigator's catalog) onto the
    /// frame stack and enter its initial step. Returns the child's initial
    /// <see cref="RealtimeIvrWorkflowStep"/> so the caller can apply it to the backend.
    /// </summary>
    /// <param name="workflowId">Id of the workflow to push. Must be known to the catalog.</param>
    /// <param name="returnToStepId">Parent-frame step id to enter when the child completes successfully (typically the subflow stage's <c>onSuccess</c>).</param>
    /// <param name="failureReturnStepId">Parent-frame step id to enter when the child exits via a failure terminal (the subflow stage's <c>onFailure</c>).</param>
    /// <param name="minVersion">Phase 2: optional lower-bound version constraint; navigator resolves the highest version <c>&gt;= minVersion</c> (and <c>&lt;= maxVersion</c> when set).</param>
    /// <param name="maxVersion">Phase 2: optional upper-bound version constraint.</param>
    Task<RealtimeIvrWorkflowStep> PushSubflowAsync(
        string workflowId,
        string? returnToStepId,
        string? failureReturnStepId,
        int? minVersion = null,
        int? maxVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pop the innermost frame and transition the now-active parent to its return /
    /// failure-return step. Returns the resumed parent step, or <see langword="null"/>
    /// when the popped frame was the root (caller should end the session).
    /// </summary>
    Task<RealtimeIvrWorkflowStep?> PopFrameAsync(bool success, CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 3: evaluate <paramref name="targetStepId"/> against the current step's
    /// per-transition <c>requires:</c> guards and the target step's stage-level guards.
    /// Returns <see cref="TransitionEvaluation.Allowed"/> when all guards pass,
    /// <see cref="TransitionEvaluation.RequiresDetour"/> when a guard fails but the
    /// workflow's <see cref="RealtimeIvrWorkflowDefinition.AuthResolvers"/> supplies a
    /// sub-workflow that can satisfy it, <see cref="TransitionEvaluation.BlockedNoResolver"/>
    /// when no resolver matches, or <see cref="TransitionEvaluation.Invalid"/> when the
    /// target isn't a declared transition from the current step.
    /// </summary>
    Task<TransitionEvaluation> EvaluateTransitionAsync(
        string targetStepId,
        CancellationToken cancellationToken = default);

    /// <summary>Look up the current step's DTMF menu binding for a digit. Pure read.</summary>
    bool TryResolveDtmfDigit(char digit, [NotNullWhen(true)] out DtmfMenuOption? option);

    /// <summary>
    /// Resolve <paramref name="option"/>'s
    /// <see cref="DtmfMenuOption.ActionToolName"/> against the current step's
    /// <see cref="RealtimeIvrWorkflowStep.AvailableTools"/>, invoke it with bound +
    /// extra arguments and the call-scoped service provider, then translate the return
    /// value to a <see cref="DtmfActionResult"/> (typed record, <c>bool Success</c>
    /// envelope, throw → <see cref="DtmfActionResult.Reject"/>). Does not apply the
    /// result — callers dispatch.
    /// </summary>
    /// <remarks>
    /// When <see cref="DtmfMenuOption.ActionToolName"/> is <see langword="null"/>, this
    /// short-circuits to a <see cref="DtmfActionResult.Transition"/> (or
    /// <see cref="DtmfActionResult.Repeat"/> if no <see cref="DtmfMenuOption.NextStepId"/>
    /// is set) without invoking any tool.
    /// </remarks>
    ValueTask<DtmfActionResult> InvokeMenuActionAsync(
        DtmfMenuOption option,
        IReadOnlyDictionary<string, object?>? extraArguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invoke a step-level digit-collection validator (typically
    /// <see cref="StepDtmfConfiguration.DigitCollectionValidator"/>) bound to a tool by
    /// reference. Same translation rules as <see cref="InvokeMenuActionAsync"/>.
    /// </summary>
    ValueTask<DtmfActionResult> InvokeActionAsync(
        Microsoft.Extensions.AI.AITool tool,
        IReadOnlyDictionary<string, object?>? boundArguments,
        IReadOnlyDictionary<string, object?>? extraArguments,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio,
        CancellationToken cancellationToken);

    /// <summary>
    /// Render the realtime-agent prompt for <see cref="CurrentStep"/> (delegates to
    /// <see cref="RealtimeIvrWorkflowDefinition.BuildPromptForStep(RealtimeIvrWorkflowStep, IvrWorkflowState?, ConversationContext?, System.Text.Json.JsonSerializerOptions?)"/>).
    /// Returns the base prompt rendered as-is when no step is current.
    /// </summary>
    string BuildCurrentStepPrompt(ConversationContext? context = null);

    /// <summary>Render the user-facing "Press X for Y" summary for a DTMF menu step.</summary>
    string BuildDtmfMenuPrompt(RealtimeIvrWorkflowStep step);

    /// <summary>
    /// Wrap each <see cref="Microsoft.Extensions.AI.AIFunction"/> in <paramref name="tools"/>
    /// with a <see cref="GuardedAIFunction"/> bound to <see cref="CurrentStep"/>'s
    /// <see cref="RealtimeIvrWorkflowStep.Guards"/> and the live <see cref="State"/>.
    /// Non-<see cref="Microsoft.Extensions.AI.AIFunction"/> tools and tools wrapped while no step is
    /// current (or while the current step has no guards) are returned unchanged.
    /// </summary>
    /// <remarks>
    /// Intended for the realtime/LLM tool path: callers register the resulting collection
    /// with the realtime agent so tool calls are sandboxed by the same workflow guards
    /// the navigator enforces on DTMF actions. Guards are read live from <see cref="CurrentStep"/>
    /// at the time of <em>this call</em>; rebind on step transitions.
    /// </remarks>
    IEnumerable<Microsoft.Extensions.AI.AITool> WrapToolsWithCurrentGuards(
        IEnumerable<Microsoft.Extensions.AI.AITool> tools);
}

/// <summary>Result of <see cref="IIvrWorkflowNavigator.TransitionTo"/>.</summary>
public readonly record struct TransitionResult(
    TransitionOutcome Outcome,
    RealtimeIvrWorkflowStep? NewStep = null,
    string? Reason = null)
{
    public bool Succeeded => Outcome == TransitionOutcome.Succeeded;

    public static TransitionResult Success(RealtimeIvrWorkflowStep step) =>
        new(TransitionOutcome.Succeeded, step);

    public static TransitionResult Invalid(string reason) =>
        new(TransitionOutcome.Invalid, Reason: reason);

    public static TransitionResult Unknown(string reason) =>
        new(TransitionOutcome.Unknown, Reason: reason);
}

public enum TransitionOutcome
{
    /// <summary>The transition was applied; <see cref="TransitionResult.NewStep"/> is the new current step.</summary>
    Succeeded,

    /// <summary>The target step exists but is not a valid transition from the current step.</summary>
    Invalid,

    /// <summary>The target step ID is not present in the workflow definition.</summary>
    Unknown,
}
