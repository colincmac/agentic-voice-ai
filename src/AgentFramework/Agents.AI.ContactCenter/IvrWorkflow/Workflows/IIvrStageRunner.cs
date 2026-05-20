using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Workflows;

/// <summary>
/// Adapter the live <see cref="IvrStageExecutor"/> talks to in order to run a single IVR stage
/// against the underlying conversation strategy (realtime backend, NLU loop, DTMF menu, agent
/// ensemble, etc.). One instance is per-call, per-strategy and is provided by an
/// <see cref="IvrStageRunnerSelector"/> when the graph is built for that call.
/// </summary>
/// <remarks>
/// <para>
/// The runner is the seam that lets the bridged
/// <see cref="Microsoft.Agents.AI.Workflows.Workflow"/> own stage <em>sequencing</em> while the
/// strategy keeps owning audio I/O, prompt pushes, tool surfacing, intent recognition, and
/// every other per-stage concern. The executor never reaches into the strategy directly — it
/// only calls <see cref="EnterStageAsync"/>, awaits an <see cref="IvrStageOutcome"/>, and
/// translates that outcome into a graph transition or workflow output.
/// </para>
/// <para>
/// Implementations are expected to:
/// <list type="bullet">
///   <item>Push the stage's prompt + (guard-wrapped) tool surface to the backend.</item>
///   <item>Wait for the backend to decide where to go next (advance-tool call, intent
///   classification, DTMF selection, ensemble handoff, …).</item>
///   <item>Return an <see cref="IvrStageOutcome"/> describing that decision; the executor
///   will call <see cref="IIvrWorkflowNavigator.TransitionTo"/> on
///   <see cref="IvrStageOutcome.Transition"/>, so implementations must <strong>not</strong>
///   transition the navigator themselves once they're being driven by the executor.</item>
/// </list>
/// </para>
/// <para>
/// Cancellation: when the supplied <see cref="CancellationToken"/> fires (e.g. the call hung
/// up), implementations should unwind their per-stage waits and either return promptly or
/// throw <see cref="System.OperationCanceledException"/>; the executor treats both as a
/// terminal end of the workflow without producing a transition.
/// </para>
/// </remarks>
public interface IIvrStageRunner
{
    /// <summary>
    /// Apply <paramref name="stage"/> to the underlying strategy and return when the stage
    /// decides to advance, complete, retry on a swapped tier, or fault.
    /// </summary>
    /// <param name="stage">The compiled stage the workflow has just entered.</param>
    /// <param name="incoming">
    /// The <see cref="IvrStageMessage"/> the executor received. Includes the previous stage id
    /// (<see cref="IvrStageMessage.FromStageId"/>) and any accumulated caller state.
    /// Implementations may use this to detect re-entries after a tier swap.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the call (or workflow run) is shutting down.</param>
    ValueTask<IvrStageOutcome> EnterStageAsync(
        CompiledIvrStage stage,
        IvrStageMessage incoming,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-stage runner lookup used by <see cref="IIvrWorkflowGraphBuilder"/> to bind each
/// generated <see cref="IvrStageExecutor"/> to a runner for the current call.
/// </summary>
/// <remarks>
/// Returning <see langword="null"/> for a stage keeps the produced executor in preview mode
/// (it stamps the incoming message and forwards it without invoking any strategy). This lets
/// the same graph builder serve both the live runtime and the existing visualization /
/// graph-shape tests.
/// </remarks>
public delegate IIvrStageRunner? IvrStageRunnerSelector(CompiledIvrStage stage);

/// <summary>
/// Result of <see cref="IIvrStageRunner.EnterStageAsync"/>. A discriminated union describing
/// what the strategy decided to do for the stage just entered.
/// </summary>
/// <remarks>
/// Use the static factory methods (<see cref="Transition"/>, <see cref="Complete"/>,
/// <see cref="Retry"/>, <see cref="Faulted"/>) instead of constructing instances directly so
/// future variants can be added without breaking call sites.
/// </remarks>
public readonly record struct IvrStageOutcome
{
    /// <summary>The kind of outcome the runner produced.</summary>
    public IvrStageOutcomeKind Kind { get; }

    /// <summary>
    /// Target stage id for <see cref="IvrStageOutcomeKind.Transition"/>; <see langword="null"/>
    /// for every other kind.
    /// </summary>
    public string? NextStageId { get; }

    /// <summary>
    /// Final workflow status for <see cref="IvrStageOutcomeKind.Complete"/> (e.g. completed /
    /// failed / cancelled). Ignored for other kinds.
    /// </summary>
    public IvrWorkflowStatus CompletionStatus { get; }

    /// <summary>
    /// Optional updated state snapshot the runner wants threaded onto the next
    /// <see cref="IvrStageMessage"/>. Treated as opaque by the executor.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? State { get; }

    /// <summary>Free-text reason for <see cref="IvrStageOutcomeKind.Faulted"/> /
    /// <see cref="IvrStageOutcomeKind.Retry"/>; useful for telemetry and logs.</summary>
    public string? Reason { get; }

    /// <summary>Exception associated with a <see cref="IvrStageOutcomeKind.Faulted"/> outcome, if any.</summary>
    public System.Exception? Exception { get; }

    private IvrStageOutcome(
        IvrStageOutcomeKind kind,
        string? nextStageId,
        IvrWorkflowStatus completionStatus,
        IReadOnlyDictionary<string, object?>? state,
        string? reason,
        System.Exception? exception)
    {
        Kind = kind;
        NextStageId = nextStageId;
        CompletionStatus = completionStatus;
        State = state;
        Reason = reason;
        Exception = exception;
    }

    /// <summary>The strategy decided to advance to <paramref name="nextStageId"/>.</summary>
    /// <param name="nextStageId">Target stage id (must be a known stage in the workflow).</param>
    /// <param name="state">Optional opaque state snapshot to thread onto the outgoing message.</param>
    public static IvrStageOutcome Transition(
        string nextStageId,
        IReadOnlyDictionary<string, object?>? state = null)
    {
        if (string.IsNullOrWhiteSpace(nextStageId))
        {
            throw new System.ArgumentException("Next stage id must be non-empty.", nameof(nextStageId));
        }

        return new IvrStageOutcome(
            IvrStageOutcomeKind.Transition,
            nextStageId,
            IvrWorkflowStatus.Running,
            state,
            reason: null,
            exception: null);
    }

    /// <summary>
    /// The stage (and the workflow) is finished — terminal stage reached, caller hung up, or
    /// the strategy decided to escalate / hand off.
    /// </summary>
    public static IvrStageOutcome Complete(
        IvrWorkflowStatus status = IvrWorkflowStatus.Completed,
        IReadOnlyDictionary<string, object?>? state = null,
        string? reason = null) =>
        new(IvrStageOutcomeKind.Complete, nextStageId: null, status, state, reason, exception: null);

    /// <summary>
    /// The stage should be re-entered (typically after a composite tier swap). The executor
    /// resends the incoming message to the same stage instead of routing along an edge.
    /// </summary>
    public static IvrStageOutcome Retry(string? reason = null) =>
        new(IvrStageOutcomeKind.Retry, nextStageId: null, IvrWorkflowStatus.Running, state: null, reason, exception: null);

    /// <summary>
    /// The runner failed and the executor should surface the fault to the workflow host
    /// (which can decide to degrade the tier or hang up). This is <strong>not</strong> a
    /// transition; the navigator is not advanced.
    /// </summary>
    public static IvrStageOutcome Faulted(string reason, System.Exception? exception = null) =>
        new(IvrStageOutcomeKind.Faulted, nextStageId: null, IvrWorkflowStatus.Failed, state: null, reason, exception);
}

/// <summary>Discriminant for <see cref="IvrStageOutcome"/>.</summary>
public enum IvrStageOutcomeKind
{
    /// <summary>Advance to <see cref="IvrStageOutcome.NextStageId"/>.</summary>
    Transition,

    /// <summary>End the workflow (terminal stage, escalation, hang-up).</summary>
    Complete,

    /// <summary>Re-enter the same stage (composite tier swap).</summary>
    Retry,

    /// <summary>Runner failed; surface to the workflow host without transitioning.</summary>
    Faulted,
}

/// <summary>
/// Workflow output emitted by <see cref="IvrStageExecutor"/> when its runner reports a
/// <see cref="IvrStageOutcomeKind.Faulted"/> outcome. The session host can recognise this
/// shape and decide whether to degrade tier or hang up — distinct from a normal
/// <see cref="IvrStageMessage"/> terminal output.
/// </summary>
/// <param name="StageId">Stage id where the fault occurred.</param>
/// <param name="Reason">Human-readable reason from <see cref="IvrStageOutcome.Reason"/>.</param>
/// <param name="Exception">Exception captured by the runner, if any.</param>
public sealed record IvrStageFaultedOutput(string StageId, string Reason, System.Exception? Exception);
