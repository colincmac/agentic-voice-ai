using System.ComponentModel;
using System.Text.Json;
using Agents.AI.Extensions.LiveVoice.Media.Audio;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

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
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly List<AITool> _commonTools = [];

    private RealtimeIvrWorkflowBuilder(string name, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _name = name;
        _promptBuilder = RealtimePrompt.CreateBuilder();
        _jsonSerializerOptions = jsonSerializerOptions ?? LiveVoiceJsonUtilities.DefaultOptions;
    }

    /// <summary>
    /// Creates a new workflow builder with the specified name.
    /// </summary>
    public static RealtimeIvrWorkflowBuilder Create(string name, JsonSerializerOptions? jsonSerializerOptions = null) => new(name, jsonSerializerOptions);


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
        if(prompt.Role is null) Throw.ArgumentNullException(nameof(prompt.Role));
        if(prompt.Personality is null) Throw.ArgumentNullException(nameof(prompt.Personality));

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

    public RealtimeIvrWorkflowBuilder WithCommonTools(params AITool[] tools)
    {
        _commonTools.AddRange(tools);
        return this;
    }


    /// <summary>
    /// Adds a workflow step using a fluent builder.
    /// </summary>
    public RealtimeIvrWorkflowBuilder AddStep(Action<RealtimeIvrStepBuilder> configure)
    {
        var builder = new RealtimeIvrStepBuilder(_jsonSerializerOptions);
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
        var builder = new RealtimeIvrStepBuilder(_jsonSerializerOptions)
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
        var builder = new RealtimeIvrStepBuilder(_jsonSerializerOptions)
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
        var steps = _commonTools is { Count: 0 } ? _steps : _steps.Select(step =>
        {
            var combinedTools = step.AvailableTools is null
                ? _commonTools
                : [.. step.AvailableTools, .. _commonTools];

            return new RealtimeIvrWorkflowStep
            {
                Id = step.Id,
                ConversationState = step.ConversationState,
                AvailableTools = combinedTools.AsReadOnly(),
                ToolRules = step.ToolRules,
                Guards = step.Guards,
                Validators = step.Validators,
                RequiredStateKeys = step.RequiredStateKeys,
                MaxRetries = step.MaxRetries,
                MaxDuration = step.MaxDuration,
                RequiredAuthLevel = step.RequiredAuthLevel,
                OnCompleted = step.OnCompleted,
                StepDtmfConfiguration = step.StepDtmfConfiguration,
            };
        }).ToList();

        return new RealtimeIvrWorkflowDefinition
        {
            Name = _name,
            BasePrompt = _promptBuilder.Build(),
            Steps = steps.AsReadOnly()
        };
    }
}

/// <summary>
/// Fluent builder for constructing individual Realtime IVR workflow steps.
/// </summary>
public sealed class RealtimeIvrStepBuilder(JsonSerializerOptions? jsonSerializerOptions = null)
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
    private StepDtmfConfiguration? _stepDtmfConfiguration;

    private readonly JsonSerializerOptions _jsonSerializerOptions = jsonSerializerOptions ?? LiveVoiceJsonUtilities.DefaultOptions;

    private static readonly JsonElement handoffSchema = AIFunctionFactory.Create(
        ([Description("The reason for the handoff")] string? reasonForHandoff) => { }).JsonSchema;

    private static string SanitizeHandoffStepName(string name) => name.Replace(" ", "_").ToLowerInvariant();

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
    /// Configures DTMF menu options for this step from a digit→next-step-ID map.
    /// Each entry becomes a declarative <see cref="DtmfMenuOption"/> whose label
    /// matches the next step ID.
    /// </summary>
    public RealtimeIvrStepBuilder WithDtmfMenu(Dictionary<char, string> options)
    {
        var menu = new Dictionary<char, DtmfMenuOption>(options.Count);
        foreach (var kv in options)
        {
            menu[kv.Key] = new DtmfMenuOption
            {
                Digit = kv.Key,
                Label = kv.Value,
                NextStepId = kv.Value,
            };
        }

        _stepDtmfConfiguration = new StepDtmfConfiguration { MenuOptions = menu };

        return this;
    }

    /// <summary>
    /// Configures DTMF menu options for this step using a fluent builder.
    /// </summary>
    public RealtimeIvrStepBuilder WithDtmfMenu(Action<DtmfMenuBuilder> configure)
    {
        var builder = new DtmfMenuBuilder();
        configure(builder);
        _stepDtmfConfiguration = builder.Build();

        return this;
    }
    private AITool CreateTransitionTool(StateTransition transition)
    {
        var functionName = $"transition_to_{transition.NextStep}";
        var description = $"Transition to the '{transition.NextStep}' step when: {transition.Condition}";
        return AIFunctionFactory.CreateDeclaration(
                functionName,
                $"Transfer conversation to {transition.NextStep}. {transition.Condition}",
                handoffSchema
            );
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
            OnCompleted = _onCompleted,
            StepDtmfConfiguration = _stepDtmfConfiguration,
        };
    }
}
/// <summary>
/// Fluent builder for constructing DTMF menu options for a workflow step.
/// </summary>
public sealed class DtmfMenuBuilder(
    char terminationDigit = '#',
    int interDigitTimeoutMs = 5000,
    int minNumberOfDigits = 1,
    int maxNumberOfDigits = 1)
{
    private readonly Dictionary<char, DtmfMenuOption> _menuOptions = new();
    private char _terminationDigit = terminationDigit;
    private int _interDigitTimeoutMs = interDigitTimeoutMs;
    private int _minNumberOfDigits = minNumberOfDigits;
    private int _maxNumberOfDigits = maxNumberOfDigits;
    private string? _promptOverride;
    private Uri? _audioFile;
    private Uri? _onErrorAudioFile;
    private string? _onErrorPrompt;

    private AITool? _digitCollectionValidator;
    private string _digitsParameterName = "digits";
    private IReadOnlyDictionary<string, object?>? _digitCollectionArguments;
    private string? _collectedStateKey;
    private string? _onValidNextStepId;
    private string? _onInvalidPrompt;
    private Uri? _onInvalidAudioFile;

    /// <summary>
    /// Declarative option: pressing <paramref name="digit"/> transitions to
    /// <paramref name="nextStepId"/> with no side-effect.
    /// </summary>
    public DtmfMenuBuilder Option(char digit, string label, string? nextStepId)
    {
        _menuOptions[digit] = new DtmfMenuOption
        {
            Digit = digit,
            Label = label,
            NextStepId = nextStepId,
        };

        return this;
    }

    /// <summary>
    /// Binds a digit to an <see cref="AITool"/> resolved by name from the owning step's
    /// <see cref="RealtimeIvrWorkflowStep.AvailableTools"/>. The tool is invoked with the
    /// supplied <paramref name="arguments"/> (resolved through the call-scoped
    /// service provider) and its return value is interpreted to decide what
    /// happens next. See <see cref="DtmfActionResult"/> for the supported shapes.
    /// </summary>
    /// <param name="digit">The DTMF digit that selects this option.</param>
    /// <param name="label">Human-readable label spoken in the menu prompt.</param>
    /// <param name="actionToolName">Name of the tool to invoke (must match an entry in the step's <see cref="RealtimeIvrWorkflowStep.AvailableTools"/>).</param>
    /// <param name="arguments">Arguments bound at configuration time.</param>
    /// <param name="nextStepId">Step to transition to on success. Ignored when the tool returns a <see cref="DtmfActionResult"/>.</param>
    /// <param name="onFailurePrompt">Prompt spoken if the tool reports a failure or throws.</param>
    /// <param name="onFailureAudioFile">Audio played if the tool reports a failure (alternative to <paramref name="onFailurePrompt"/>).</param>
    public DtmfMenuBuilder Option(
        char digit,
        string label,
        string actionToolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? nextStepId = null,
        string? onFailurePrompt = null,
        Uri? onFailureAudioFile = null)
    {
        _menuOptions[digit] = new DtmfMenuOption
        {
            Digit = digit,
            Label = label,
            ActionToolName = actionToolName,
            Arguments = arguments,
            NextStepId = nextStepId,
            OnFailurePrompt = onFailurePrompt,
            OnFailureAudioFile = onFailureAudioFile,
        };

        return this;
    }

    /// <summary>
    /// Adds a pre-built <see cref="DtmfMenuOption"/>.
    /// </summary>
    public DtmfMenuBuilder Option(DtmfMenuOption option)
    {
        _menuOptions[option.Digit] = option;

        return this;
    }

    /// <summary>
    /// Configures the step to collect a sequence of digits (e.g. an account number)
    /// and validate them with the supplied <see cref="AITool"/> once the buffer is
    /// terminated or full.
    /// </summary>
    /// <param name="validator">
    /// Tool invoked with the collected digits. The strategy passes the digits as
    /// argument <paramref name="digitsParameterName"/> together with any
    /// <paramref name="arguments"/>. The tool may return a <see cref="DtmfActionResult"/>
    /// for fine-grained control, or any envelope with a <c>bool Success</c> property
    /// (such as <c>CallControlResult</c>) — success transitions to
    /// <paramref name="onValidNextStepId"/>, failure plays <paramref name="onInvalidPrompt"/>.
    /// </param>
    /// <param name="digitsParameterName">Name of the tool argument that receives the collected digits.</param>
    /// <param name="collectedStateKey">State key under which to store the digits on success. Defaults to <c>"{stepId}_collected"</c>.</param>
    /// <param name="onValidNextStepId">Step to transition to when the validator reports success.</param>
    /// <param name="onInvalidPrompt">Prompt spoken when the validator reports failure.</param>
    /// <param name="onInvalidAudioFile">Audio file played when the validator reports failure.</param>
    /// <param name="arguments">Additional bound arguments passed to the validator.</param>
    public DtmfMenuBuilder ValidateWith(
        AITool validator,
        string digitsParameterName = "digits",
        string? collectedStateKey = null,
        string? onValidNextStepId = null,
        string? onInvalidPrompt = null,
        Uri? onInvalidAudioFile = null,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        _digitCollectionValidator = validator;
        _digitsParameterName = digitsParameterName;
        _digitCollectionArguments = arguments;
        _collectedStateKey = collectedStateKey;
        _onValidNextStepId = onValidNextStepId;
        _onInvalidPrompt = onInvalidPrompt;
        _onInvalidAudioFile = onInvalidAudioFile;

        return this;
    }

    /// <summary>
    /// Sets the termination digit used to signal the end of digit input.
    /// </summary>
    public DtmfMenuBuilder WithTerminationDigit(char digit)
    {
        _terminationDigit = digit;

        return this;
    }

    /// <summary>
    /// Sets the inter-digit timeout in milliseconds.
    /// </summary>
    public DtmfMenuBuilder WithInterDigitTimeoutMs(int timeoutMs)
    {
        _interDigitTimeoutMs = timeoutMs;

        return this;
    }

    /// <summary>
    /// Sets the minimum number of digits expected.
    /// </summary>
    public DtmfMenuBuilder WithMinNumberOfDigits(int min)
    {
        _minNumberOfDigits = min;

        return this;
    }

    /// <summary>
    /// Sets the maximum number of digits expected.
    /// </summary>
    public DtmfMenuBuilder WithMaxNumberOfDigits(int max)
    {
        _maxNumberOfDigits = max;

        return this;
    }

    /// <summary>
    /// Sets a prompt override for this DTMF step.
    /// </summary>
    public DtmfMenuBuilder WithPromptOverride(string prompt)
    {
        _promptOverride = prompt;

        return this;
    }

    /// <summary>
    /// Sets an audio file URI to play for this DTMF step.
    /// </summary>
    public DtmfMenuBuilder WithAudioFile(Uri audioFile)
    {
        _audioFile = audioFile;

        return this;
    }

    /// <summary>
    /// Sets an audio file URI to play when an error occurs.
    /// </summary>
    public DtmfMenuBuilder WithOnErrorAudioFile(Uri onErrorAudioFile)
    {
        _onErrorAudioFile = onErrorAudioFile;

        return this;
    }

    /// <summary>
    /// Sets a prompt to play when an error occurs.
    /// </summary>
    public DtmfMenuBuilder WithOnErrorPrompt(string prompt)
    {
        _onErrorPrompt = prompt;

        return this;
    }


    internal StepDtmfConfiguration Build()
    {
        var config = new StepDtmfConfiguration(
            _terminationDigit,
            _interDigitTimeoutMs,
            _minNumberOfDigits,
            _maxNumberOfDigits,
            _promptOverride)
        {
            AudioFile = _audioFile,
            OnErrorAudioFile = _onErrorAudioFile,
            OnErrorPrompt = _onErrorPrompt,
            MenuOptions = _menuOptions.Count > 0
                ? new Dictionary<char, DtmfMenuOption>(_menuOptions)
                : null,
            DigitCollectionValidator = _digitCollectionValidator,
            DigitsParameterName = _digitsParameterName,
            DigitCollectionArguments = _digitCollectionArguments,
            CollectedStateKey = _collectedStateKey,
            OnValidNextStepId = _onValidNextStepId,
            OnInvalidPrompt = _onInvalidPrompt,
            OnInvalidAudioFile = _onInvalidAudioFile,
        };

        return config;
    }
}
