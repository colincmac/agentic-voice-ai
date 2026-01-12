using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;

/// <summary>
/// Represents a completed IVR workflow definition.
/// </summary>
public sealed class IvrWorkflowDefinition
{

    internal IvrWorkflowDefinition(
        string name,
        IReadOnlyList<IIvrWorkflowStep> steps,
        string? welcomeMessage,
        string? completionMessage,
        string? failureMessage)
    {
        Name = name;
        Steps = steps;
        WelcomeMessage = welcomeMessage;
        CompletionMessage = completionMessage;
        FailureMessage = failureMessage;
    }

    /// <summary>
    /// Gets the name of this workflow.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the ordered steps in this workflow.
    /// </summary>
    public IReadOnlyList<IIvrWorkflowStep> Steps { get; }

    /// <summary>
    /// Gets the welcome message shown when the workflow starts.
    /// </summary>
    public string? WelcomeMessage { get; }

    /// <summary>
    /// Gets the message shown when the workflow completes successfully.
    /// </summary>
    public string? CompletionMessage { get; }

    /// <summary>
    /// Gets the message shown when the workflow fails.
    /// </summary>
    public string? FailureMessage { get; }

    /// <summary>
    /// Gets a step by name.
    /// </summary>
    public IIvrWorkflowStep? GetStep(string name) => Steps.FirstOrDefault(s => s.Name == name);

    /// <summary>
    /// Gets the index of a step by name.
    /// </summary>
    public int GetStepIndex(string name) => Steps.ToList().FindIndex(s => s.Name == name);
}

/// <summary>
/// Fluent builder for constructing IVR workflow definitions.
/// </summary>
public sealed class IvrWorkflowBuilder
{
    private readonly string _name;
    private readonly List<IIvrWorkflowStep> _steps = [];
    private string? _welcomeMessage;
    private string? _completionMessage;
    private string? _failureMessage;
    internal const string FunctionPrefix = "handoff_to_";
    public string? HandoffInstructions { get; set; } =
     $"""
              You are one agent in a multi-agent system. You can hand off the conversation to another agent if appropriate. Handoffs are achieved
              by calling a handoff function, named in the form `{FunctionPrefix}<agent_id>`; the description of the function provides details on the
              target agent of that handoff. Handoffs between agents are handled seamlessly in the background; never mention or narrate these handoffs
              in your conversation with the user.
              """;

    private IvrWorkflowBuilder(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Creates a new workflow builder with the specified name.
    /// </summary>
    public static IvrWorkflowBuilder Create(string name) => new(name);

    /// <summary>
    /// Sets the welcome message for the workflow.
    /// </summary>
    public IvrWorkflowBuilder WithWelcomeMessage(string message)
    {
        _welcomeMessage = message;
        return this;
    }

    /// <summary>
    /// Sets the completion message for the workflow.
    /// </summary>
    public IvrWorkflowBuilder WithCompletionMessage(string message)
    {
        _completionMessage = message;
        return this;
    }

    /// <summary>
    /// Sets the failure message for the workflow.
    /// </summary>
    public IvrWorkflowBuilder WithFailureMessage(string message)
    {
        _failureMessage = message;
        return this;
    }

    /// <summary>
    /// Adds a pre-built step to the workflow.
    /// </summary>
    public IvrWorkflowBuilder AddStep(IIvrWorkflowStep step)
    {
        _steps.Add(step);
        return this;
    }

    /// <summary>
    /// Adds an input collection step.
    /// </summary>
    public IvrWorkflowBuilder AddInputStep(
        string name,
        string orchestratorInstructions,
        string voiceAgentInstructions,
        string stateKey,
        Action<IvrStepBuilder>? configure = null)
    {
        var builder = new IvrStepBuilder(name, orchestratorInstructions, voiceAgentInstructions, stateKey);
        configure?.Invoke(builder);
        _steps.Add(builder.Build());
        return this;
    }


    /// <summary>
    /// Builds the workflow definition.
    /// </summary>
    public IvrWorkflowDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            throw new InvalidOperationException("Workflow name is required.");
        }

        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("Workflow must have at least one step.");
        }

        return new IvrWorkflowDefinition(
            _name,
            _steps.AsReadOnly(),
            _welcomeMessage,
            _completionMessage,
            _failureMessage);
    }
}


/// <summary>
/// Builder for input collection steps.
/// </summary>
public sealed class IvrStepBuilder
{
    private readonly string _name;
    private readonly string _orchestratorInstructions;
    private readonly string _voiceAgentInstructions;
    private AuthenticationLevel _requiredAuthLevel = AuthenticationLevel.None;

    private readonly string _stateKey;
    private readonly List<IIvrStepGuard> _guards = [];
    private readonly List<IIvrStepValidator> _validators = [];
    private int _maxRetries = 3;
    private int? _maxDurationInSeconds;
    private string _retryPromptTemplate = "I didn't catch that. {error}";

    public IvrStepBuilder(string name, string orchestratorInstructions, string voiceAgentInstructions, string stateKey)
    {
        _name = name;
        _orchestratorInstructions = orchestratorInstructions;
        _voiceAgentInstructions = voiceAgentInstructions;
        _stateKey = stateKey;
    }

    public IvrStepBuilder WithMaxRetries(int maxRetries)
    {
        _maxRetries = maxRetries;
        return this;
    }

    public IvrStepBuilder WithRequiredAuthLevel(AuthenticationLevel authLevel)
    {
        _requiredAuthLevel = authLevel;
        return this;
    }
    public IvrStepBuilder WithMaxDuration(int maxDurationInSeconds)
    {
        _maxDurationInSeconds = maxDurationInSeconds;
        return this;
    }
    public IvrStepBuilder WithRetryPrompt(string template)
    {
        _retryPromptTemplate = template;
        return this;
    }

    public IvrStepBuilder RequiresState(string stateKey, string? failureMessage = null)
    {
        _guards.Add(new RequiredStateGuard(stateKey, failureMessage));
        return this;
    }

    public IvrStepBuilder RequiresPreviousStep(string stepName)
    {
        _guards.Add(new PreviousStepCompletedGuard(stepName));
        return this;
    }

    public IvrStepBuilder WithGuard(IIvrStepGuard guard)
    {
        _guards.Add(guard);
        return this;
    }
    public IvrStepBuilder WithGuard(Func<IvrWorkflowState, bool> predicate, string failureMessage)
    {
        _guards.Add(new PredicateGuard(predicate, failureMessage));
        return this;
    }

    public IvrStepBuilder WithValidator(IIvrStepValidator validator)
    {
        _validators.Add(validator);
        return this;
    }

    public IvrStepBuilder WithValidator(Func<IvrWorkflowState, bool> predicate, string failureMessage)
    {
        _validators.Add(new PredicateValidator(predicate, failureMessage));
        return this;
    }

    internal IIvrWorkflowStep Build()
    {
        var step = new InputCollectionStep(_name, _voiceAgentInstructions, _orchestratorInstructions, _stateKey, _maxRetries, _retryPromptTemplate,  _maxDurationInSeconds.HasValue ? TimeSpan.FromSeconds(_maxDurationInSeconds.Value) : null, _requiredAuthLevel);
        foreach (var guard in _guards)
        {
            step.AddGuard(guard);
        }

        foreach (var validator in _validators)
        {
            step.AddValidator(validator);
        }

        return step;
    }

    private sealed class InputCollectionStep : IvrWorkflowStepBase
    {
        private readonly string _stateKey;

        public InputCollectionStep(string name, string voiceAgentInstructions, string orchestratorInstructions, string stateKey, int maxRetries, string retryPromptTemplate, TimeSpan? maxDuration = null, AuthenticationLevel? requiredAuthLevel = AuthenticationLevel.None) : base(name, voiceAgentInstructions, orchestratorInstructions)
        {
            _stateKey = stateKey;
            MaxRetries = maxRetries;
            RetryPromptTemplate = retryPromptTemplate;
            MaxDuration = maxDuration;
            RequiredAuthLevel = requiredAuthLevel ?? AuthenticationLevel.None;
        }

        public override Task<IvrStepResult> ExecuteAsync(
            IvrWorkflowState state,
            string? userInput,
            CancellationToken cancellationToken = default)
        {

            return Task.FromResult(IvrStepResult.Succeeded());
        }
    }
}
