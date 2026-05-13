using System.Diagnostics.CodeAnalysis;
using System.Text;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <inheritdoc cref="IIvrWorkflowNavigator"/>
public sealed class IvrWorkflowNavigator(
    RealtimeIvrWorkflowDefinition definition,
    IvrWorkflowState state,
    IServiceProvider services,
    ILogger<IvrWorkflowNavigator>? logger = null) : IIvrWorkflowNavigator
{
    private readonly IServiceProvider _services = services;
    private readonly ILogger _logger = logger ?? NullLogger<IvrWorkflowNavigator>.Instance;

    public RealtimeIvrWorkflowDefinition Definition { get; } = definition;

    public IvrWorkflowState State { get; } = state;

    public RealtimeIvrWorkflowStep? CurrentStep =>
        State.CurrentStepName is { } id ? Definition.GetStep(id) : null;

    public RealtimeIvrWorkflowStep EnterInitialStep()
    {
        var stepId = State.CurrentStepName ?? Definition.InitialStepId;
        var step = Definition.GetStep(stepId)
            ?? throw new InvalidOperationException($"Step '{stepId}' not found in workflow '{Definition.Name}'.");

        State.CurrentStepName = step.Id;
        State.CurrentStepIndex = Definition.GetStepIndex(step.Id);
        State.StepStartedAt = DateTimeOffset.UtcNow;
        if (State.Status is IvrWorkflowStatus.NotStarted)
        {
            State.Status = IvrWorkflowStatus.Running;
        }

        return step;
    }

    public TransitionResult TransitionTo(string targetStepId)
    {
        var current = CurrentStep;
        if (current is null)
        {
            return TransitionResult.Invalid("no current step");
        }

        if (!current.ValidTransitions.Contains(targetStepId, StringComparer.Ordinal))
        {
            return TransitionResult.Invalid(
                $"'{targetStepId}' is not a valid transition from '{current.Id}'");
        }

        var target = Definition.GetStep(targetStepId);
        if (target is null)
        {
            return TransitionResult.Unknown($"step '{targetStepId}' not in workflow '{Definition.Name}'");
        }

        State.MarkStepCompleted(current.Id);
        State.CurrentStepName = target.Id;
        State.CurrentStepIndex = Definition.GetStepIndex(target.Id);
        State.StepStartedAt = DateTimeOffset.UtcNow;
        return TransitionResult.Success(target);
    }

    public void Complete(IvrWorkflowStatus status = IvrWorkflowStatus.Completed)
    {
        if (CurrentStep is { } step)
        {
            State.MarkStepCompleted(step.Id);
        }
        State.Status = status;
    }

    public bool TryResolveDtmfDigit(char digit, [NotNullWhen(true)] out DtmfMenuOption? option)
    {
        option = null;
        return CurrentStep?.StepDtmfConfiguration?.MenuOptions is { } menu
            && menu.TryGetValue(digit, out option);
    }

    public ValueTask<DtmfActionResult> InvokeMenuActionAsync(
        DtmfMenuOption option,
        IReadOnlyDictionary<string, object?>? extraArguments,
        CancellationToken cancellationToken)
    {
        if (option.ActionToolName is null)
        {
            return ValueTask.FromResult<DtmfActionResult>(
                option.NextStepId is { } next
                    ? new DtmfActionResult.Transition(next)
                    : new DtmfActionResult.Repeat());
        }

        var step = CurrentStep
            ?? throw new InvalidOperationException("No current step; cannot resolve menu action.");

        var tool = step.AvailableTools?.FirstOrDefault(
            t => string.Equals(t.Name, option.ActionToolName, StringComparison.Ordinal));

        if (tool is null)
        {
            _logger.LogWarning(
                "DTMF option for digit '{Digit}' references tool '{ToolName}', which is not present in step '{StepId}' AvailableTools.",
                option.Digit, option.ActionToolName, step.Id);
            return ValueTask.FromResult<DtmfActionResult>(
                new DtmfActionResult.Reject(option.OnFailurePrompt, option.OnFailureAudioFile));
        }

        return InvokeActionAsync(
            tool,
            option.Arguments,
            extraArguments,
            option.NextStepId,
            option.OnFailurePrompt,
            option.OnFailureAudioFile,
            cancellationToken);
    }

    public async ValueTask<DtmfActionResult> InvokeActionAsync(
        AITool tool,
        IReadOnlyDictionary<string, object?>? boundArguments,
        IReadOnlyDictionary<string, object?>? extraArguments,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio,
        CancellationToken cancellationToken)
    {
        if (tool is not AIFunction fn)
        {
            _logger.LogWarning(
                "DTMF action tool '{Name}' is not an AIFunction and cannot be invoked.",
                tool.Name);
            return new DtmfActionResult.Reject(failurePrompt, failureAudio);
        }

        if (CurrentStep is { Guards: { Count: > 0 } guards } gatedStep)
        {
            for (var i = 0; i < guards.Count; i++)
            {
                var guard = guards[i];
                var gr = await guard.EvaluateAsync(State, cancellationToken).ConfigureAwait(false);
                if (!gr.Passed)
                {
                    _logger.LogInformation(
                        "DTMF action '{Tool}' blocked by guard '{GuardType}' on step '{Step}': {Reason}",
                        fn.Name, guard.GetType().Name, gatedStep.Id, gr.FailureReason);
                    return new DtmfActionResult.Reject(failurePrompt, failureAudio);
                }
            }
        }

        var args = new AIFunctionArguments { Services = _services };
        if (boundArguments is not null)
        {
            foreach (var kv in boundArguments)
            {
                args[kv.Key] = kv.Value;
            }
        }
        if (extraArguments is not null)
        {
            foreach (var kv in extraArguments)
            {
                args[kv.Key] = kv.Value;
            }
        }

        object? raw;
        try
        {
            raw = await fn.InvokeAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DTMF action tool '{Name}' threw; treating as rejection.", tool.Name);
            return new DtmfActionResult.Reject(failurePrompt, failureAudio);
        }

        return InterpretResult(raw, successNextStepId, failurePrompt, failureAudio);
    }

    public string BuildCurrentStepPrompt(ConversationContext? context = null) =>
        CurrentStep is { } step
            ? Definition.BuildPromptForStep(step, State, context)
            : RealtimeAIPromptTemplate.Render(Definition.BasePrompt);

    public IEnumerable<AITool> WrapToolsWithCurrentGuards(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (CurrentStep is not { Guards: { Count: > 0 } guards })
        {
            return tools;
        }

        return GuardedAIFunction.WrapTools(tools, guards, () => State);
    }

    public string BuildDtmfMenuPrompt(RealtimeIvrWorkflowStep step)
    {
        var sb = new StringBuilder();
        var intro = step.ConversationState.Description ?? step.ConversationState.Goal;
        if (!string.IsNullOrWhiteSpace(intro))
        {
            sb.AppendLine(intro);
        }
        if (step.StepDtmfConfiguration?.MenuOptions is { Count: > 0 } menu)
        {
            foreach (var (digit, option) in menu)
            {
                sb.AppendLine($"Press {digit} for {option.Label}.");
            }
        }
        return sb.ToString();
    }

    private static DtmfActionResult InterpretResult(
        object? raw,
        string? successNextStepId,
        string? failurePrompt,
        Uri? failureAudio)
    {
        switch (raw)
        {
            case null:
                return successNextStepId is not null
                    ? new DtmfActionResult.Transition(successNextStepId)
                    : new DtmfActionResult.Repeat();
            case DtmfActionResult typed:
                return typed;
        }

        // Reflection fallback for envelopes like CallControlResult { bool Success; string Message; }.
        var type = raw.GetType();
        var successProp = type.GetProperty("Success") ?? type.GetProperty("IsSuccess");
        if (successProp is not null && successProp.PropertyType == typeof(bool))
        {
            var success = (bool)(successProp.GetValue(raw) ?? false);
            if (!success)
            {
                return new DtmfActionResult.Reject(failurePrompt, failureAudio);
            }
        }

        return successNextStepId is not null
            ? new DtmfActionResult.Transition(successNextStepId)
            : new DtmfActionResult.Repeat();
    }
}
