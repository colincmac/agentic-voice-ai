using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Fluent builder for constructing Realtime IVR workflow definitions
/// that integrate with the Realtime AI prompt system.
/// </summary>
public sealed class RealtimeIvrWorkflowBuilder
{
    private readonly string _name;
    private readonly List<RealtimeIvrWorkflowStep> _steps = [];
    private RealtimePromptBuilder _promptBuilder;

    private RealtimeIvrWorkflowBuilder(string name)
    {
        _name = name;
        _promptBuilder = RealtimePrompt.CreateBuilder();
    }

    /// <summary>
    /// Creates a new workflow builder with the specified name.
    /// </summary>
    public static RealtimeIvrWorkflowBuilder Create(string name) => new(name);

    /// <summary>
    /// Configures the base prompt for all steps using a fluent builder.
    /// </summary>
    public RealtimeIvrWorkflowBuilder WithBasePrompt(Action<RealtimePromptBuilder> configure)
    {
        configure(_promptBuilder);

        return this;
    }

    /// <summary>
    /// Sets the base prompt directly.
    /// </summary>
    public RealtimeIvrWorkflowBuilder WithBasePrompt(RealtimePrompt prompt)
    {
        _promptBuilder = RealtimePrompt.CreateBuilder();
        // Copy prompt properties - we'll rebuild from the provided prompt
        _promptBuilder
            .WithRole(prompt.Role!)
            .WithPersonality(prompt.Personality!)
            .WithContext(prompt.Context ?? string.Empty);

        if (prompt.ReferencePronunciations is not null)
        {
            foreach (var p in prompt.ReferencePronunciations)
            {
                _promptBuilder.AddPronunciation(p.Word, p.Pronunciation);
            }
        }

        if (prompt.Tools is not null)
        {
            _promptBuilder.WithTools(prompt.Tools);
        }

        if (prompt.Instructions is not null)
        {
            _promptBuilder.WithInstructions(prompt.Instructions);
        }

        if (prompt.Phrases is not null)
        {
            _promptBuilder.WithSamplePhrases(prompt.Phrases);
        }

        if (prompt.Safety is not null)
        {
            _promptBuilder.WithSafety(prompt.Safety);
        }

        return this;
    }

    /// <summary>
    /// Adds a workflow step using a fluent builder.
    /// </summary>
    public RealtimeIvrWorkflowBuilder AddStep(Action<RealtimeIvrStepBuilder> configure)
    {
        var builder = new RealtimeIvrStepBuilder();
        configure(builder);
        _steps.Add(builder.Build());

        return this;
    }

    /// <summary>
    /// Adds a pre-built workflow step.
    /// </summary>
    public RealtimeIvrWorkflowBuilder AddStep(RealtimeIvrWorkflowStep step)
    {
        _steps.Add(step);

        return this;
    }

    /// <summary>
    /// Adds a greeting step as the first step.
    /// </summary>
    public RealtimeIvrWorkflowBuilder WithGreeting(
        string welcomeMessage,
        Action<RealtimeIvrStepBuilder>? configure = null)
    {
        var builder = new RealtimeIvrStepBuilder()
            .WithId("1_greeting")
            .WithGoal("Set tone and invite the reason for calling")
            .WithDescription("Greet the caller warmly and identify the service")
            .AddInstruction($"Greet the caller: \"{welcomeMessage}\"")
            .AddInstruction("Keep the opener brief and invite the caller's goal")
            .AddExample(welcomeMessage)
            .ExitWhen("Caller states an initial goal or symptom");

        if (_steps.Count > 0)
        {
            builder.TransitionTo(_steps[0].Id, "After greeting is complete");
        }

        configure?.Invoke(builder);

        // Insert at the beginning
        _steps.Insert(0, builder.Build());

        return this;
    }

    /// <summary>
    /// Adds a confirmation/closing step.
    /// </summary>
    public RealtimeIvrWorkflowBuilder WithClosing(
        string completionMessage,
        Action<RealtimeIvrStepBuilder>? configure = null)
    {
        var stepId = $"{_steps.Count + 1}_closing";
        var builder = new RealtimeIvrStepBuilder()
            .WithId(stepId)
            .WithGoal("Confirm outcome and end cleanly")
            .WithDescription("Restate the result and any next steps, invite final questions")
            .AddInstruction($"Completion message: \"{completionMessage}\"")
            .AddInstruction("Invite final questions; close politely if none")
            .AddExample(completionMessage)
            .ExitWhen("Caller declines more help");

        configure?.Invoke(builder);
        _steps.Add(builder.Build());

        return this;
    }

    /// <summary>
    /// Builds the workflow definition.
    /// </summary>
    public RealtimeIvrWorkflowDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            throw new InvalidOperationException("Workflow name is required.");
        }

        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("Workflow must have at least one step.");
        }

        return new RealtimeIvrWorkflowDefinition
        {
            Name = _name,
            BasePrompt = _promptBuilder.Build(),
            Steps = _steps.AsReadOnly()
        };
    }
}

/// <summary>
/// Fluent builder for constructing individual Realtime IVR workflow steps.
/// </summary>
public sealed class RealtimeIvrStepBuilder
{
    private string _id = string.Empty;
    private string _description = string.Empty;
    private string? _goal;
    private readonly List<string> _instructions = [];
    private readonly List<string> _examples = [];
    private string? _exitWhen;
    private readonly List<StateTransition> _transitions = [];
    private readonly List<AITool> _tools = [];
    private readonly List<ToolUsageRule> _toolRules = [];
    private readonly List<IIvrStepGuard> _guards = [];
    private readonly List<IIvrStepValidator> _validators = [];
    private readonly List<string> _requiredStateKeys = [];
    private int _maxRetries = 3;
    private TimeSpan? _maxDuration;
    private AuthenticationLevel _requiredAuthLevel = AuthenticationLevel.None;
    private Func<IvrWorkflowState, CancellationToken, Task>? _onCompleted;

    /// <summary>
    /// Sets the step identifier.
    /// </summary>
    public RealtimeIvrStepBuilder WithId(string id)
    {
        _id = id;

        return this;
    }

    /// <summary>
    /// Sets the step description.
    /// </summary>
    public RealtimeIvrStepBuilder WithDescription(string description)
    {
        _description = description;

        return this;
    }

    /// <summary>
    /// Sets the goal for this step.
    /// </summary>
    public RealtimeIvrStepBuilder WithGoal(string goal)
    {
        _goal = goal;

        return this;
    }

    /// <summary>
    /// Adds an instruction for this step.
    /// </summary>
    public RealtimeIvrStepBuilder AddInstruction(string instruction)
    {
        _instructions.Add(instruction);

        return this;
    }

    /// <summary>
    /// Adds multiple instructions.
    /// </summary>
    public RealtimeIvrStepBuilder AddInstructions(params string[] instructions)
    {
        _instructions.AddRange(instructions);

        return this;
    }

    /// <summary>
    /// Adds an example phrase.
    /// </summary>
    public RealtimeIvrStepBuilder AddExample(string example)
    {
        _examples.Add(example);

        return this;
    }

    /// <summary>
    /// Adds multiple example phrases.
    /// </summary>
    public RealtimeIvrStepBuilder AddExamples(params string[] examples)
    {
        _examples.AddRange(examples);

        return this;
    }

    /// <summary>
    /// Sets the exit condition.
    /// </summary>
    public RealtimeIvrStepBuilder ExitWhen(string condition)
    {
        _exitWhen = condition;

        return this;
    }

    /// <summary>
    /// Adds a transition to another step.
    /// </summary>
    public RealtimeIvrStepBuilder TransitionTo(string nextStepId, string condition)
    {
        _transitions.Add(new StateTransition { NextStep = nextStepId, Condition = condition });

        return this;
    }

    /// <summary>
    /// Adds a tool available during this step.
    /// </summary>
    public RealtimeIvrStepBuilder WithTool(AITool tool)
    {
        _tools.Add(tool);

        return this;
    }

    /// <summary>
    /// Adds multiple tools available during this step.
    /// </summary>
    public RealtimeIvrStepBuilder WithTools(params AITool[] tools)
    {
        _tools.AddRange(tools);

        return this;
    }

    /// <summary>
    /// Adds a proactive tool with usage rules.
    /// </summary>
    public RealtimeIvrStepBuilder WithProactiveTool(AITool tool, string useWhen, string? doNotUseWhen = null)
    {
        _tools.Add(tool);
        _toolRules.Add(new ToolUsageRule
        {
            Name = tool.Name,
            UseWhen = useWhen,
            DoNotUseWhen = doNotUseWhen,
            Behavior = ToolBehavior.Proactive
        });

        return this;
    }

    /// <summary>
    /// Adds a tool that requires confirmation before calling.
    /// </summary>
    public RealtimeIvrStepBuilder WithConfirmationTool(
        AITool tool,
        string useWhen,
        string confirmationPhrase,
        string? doNotUseWhen = null)
    {
        _tools.Add(tool);
        _toolRules.Add(new ToolUsageRule
        {
            Name = tool.Name,
            UseWhen = useWhen,
            DoNotUseWhen = doNotUseWhen,
            Behavior = ToolBehavior.ConfirmationFirst,
            ConfirmationPhrase = confirmationPhrase
        });

        return this;
    }

    /// <summary>
    /// Adds a tool with preamble phrases.
    /// </summary>
    public RealtimeIvrStepBuilder WithPreambleTool(
        AITool tool,
        string useWhen,
        IEnumerable<string> preamblePhrases,
        string? doNotUseWhen = null)
    {
        _tools.Add(tool);
        _toolRules.Add(new ToolUsageRule
        {
            Name = tool.Name,
            UseWhen = useWhen,
            DoNotUseWhen = doNotUseWhen,
            Behavior = ToolBehavior.Preambles,
            PreamblePhrases = [.. preamblePhrases]
        });

        return this;
    }

    /// <summary>
    /// Adds a guard that must pass before this step can execute.
    /// </summary>
    public RealtimeIvrStepBuilder WithGuard(IIvrStepGuard guard)
    {
        _guards.Add(guard);

        return this;
    }

    /// <summary>
    /// Adds a predicate-based guard.
    /// </summary>
    public RealtimeIvrStepBuilder WithGuard(Func<IvrWorkflowState, bool> predicate, string failureMessage)
    {
        _guards.Add(new PredicateGuard(predicate, failureMessage));

        return this;
    }

    /// <summary>
    /// Requires a previous step to be completed.
    /// </summary>
    public RealtimeIvrStepBuilder RequiresPreviousStep(string stepId)
    {
        _guards.Add(new PreviousStepCompletedGuard(stepId));

        return this;
    }

    /// <summary>
    /// Requires specific state keys to be present.
    /// </summary>
    public RealtimeIvrStepBuilder RequiresState(params string[] stateKeys)
    {
        foreach (var key in stateKeys)
        {
            _guards.Add(new RequiredStateGuard(key));
        }

        return this;
    }

    /// <summary>
    /// Adds a validator for this step.
    /// </summary>
    public RealtimeIvrStepBuilder WithValidator(IIvrStepValidator validator)
    {
        _validators.Add(validator);

        return this;
    }

    /// <summary>
    /// Adds a predicate-based validator.
    /// </summary>
    public RealtimeIvrStepBuilder WithValidator(Func<IvrWorkflowState, bool> predicate, string failureMessage)
    {
        _validators.Add(new PredicateValidator(predicate, failureMessage));

        return this;
    }

    /// <summary>
    /// Specifies state keys that must be collected during this step.
    /// </summary>
    public RealtimeIvrStepBuilder CollectsState(params string[] stateKeys)
    {
        _requiredStateKeys.AddRange(stateKeys);

        return this;
    }

    /// <summary>
    /// Sets the maximum number of retries.
    /// </summary>
    public RealtimeIvrStepBuilder WithMaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;

        return this;
    }

    /// <summary>
    /// Sets the maximum duration for this step.
    /// </summary>
    public RealtimeIvrStepBuilder WithMaxDuration(TimeSpan duration)
    {
        _maxDuration = duration;

        return this;
    }

    /// <summary>
    /// Sets the required authentication level.
    /// </summary>
    public RealtimeIvrStepBuilder RequiresAuth(AuthenticationLevel level)
    {
        _requiredAuthLevel = level;

        return this;
    }

    /// <summary>
    /// Sets a callback to execute when this step completes.
    /// </summary>
    public RealtimeIvrStepBuilder OnCompleted(Func<IvrWorkflowState, CancellationToken, Task> callback)
    {
        _onCompleted = callback;

        return this;
    }

    /// <summary>
    /// Sets a synchronous callback to execute when this step completes.
    /// </summary>
    public RealtimeIvrStepBuilder OnCompleted(Action<IvrWorkflowState> callback)
    {
        _onCompleted = (state, _) =>
        {
            callback(state);

            return Task.CompletedTask;
        };

        return this;
    }

    /// <summary>
    /// Builds the workflow step.
    /// </summary>
    public RealtimeIvrWorkflowStep Build()
    {
        if (string.IsNullOrWhiteSpace(_id))
        {
            throw new InvalidOperationException("Step ID is required.");
        }

        if (string.IsNullOrWhiteSpace(_description))
        {
            throw new InvalidOperationException("Step description is required.");
        }

        if (_instructions.Count == 0)
        {
            throw new InvalidOperationException("At least one instruction is required.");
        }

        var conversationState = new ConversationState
        {
            Id = _id,
            Description = _description,
            Goal = _goal,
            Instructions = _instructions.AsReadOnly(),
            Examples = _examples.Count > 0 ? _examples.AsReadOnly() : null,
            ExitWhen = _exitWhen,
            Transitions = _transitions.Count > 0 ? _transitions.AsReadOnly() : null
        };

        return new RealtimeIvrWorkflowStep
        {
            Id = _id,
            ConversationState = conversationState,
            AvailableTools = _tools.Count > 0 ? _tools.AsReadOnly() : null,
            ToolRules = _toolRules.Count > 0 ? _toolRules.AsReadOnly() : null,
            Guards = _guards.AsReadOnly(),
            Validators = _validators.AsReadOnly(),
            RequiredStateKeys = _requiredStateKeys.AsReadOnly(),
            MaxRetries = _maxRetries,
            MaxDuration = _maxDuration,
            RequiredAuthLevel = _requiredAuthLevel,
            OnCompleted = _onCompleted
        };
    }
}
