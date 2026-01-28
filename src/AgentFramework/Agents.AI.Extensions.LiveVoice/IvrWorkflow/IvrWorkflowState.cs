using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.LiveVoice.IvrWorkflow;


/// <summary>
/// Represents the state of an IVR workflow, maintaining data collected across steps.
/// </summary>
public sealed class IvrWorkflowState
{
    private readonly ConcurrentDictionary<string, object?> _data = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _timestamps = new();
    private readonly List<string> _completedSteps = [];
    private readonly List<ChatMessage> _transcript = [];
    private readonly List<RealtimeConversationUtterance> _conversationHistory = [];
    private readonly Lock _lock = new();
    private string? _workflowId;
    /// <summary>
    /// Gets a thread-safe snapshot of the transcript messages.
    /// </summary>
    public IReadOnlyList<ChatMessage> Transcript
    {
        get
        {
            lock (_lock)
            {
                return [.. _transcript];
            }
        }
    }

    /// <summary>
    /// Gets a thread-safe snapshot of the conversation history with timing information.
    /// </summary>
    public IReadOnlyList<RealtimeConversationUtterance> ConversationHistory
    {
        get
        {
            lock (_lock)
            {
                return [.. _conversationHistory];
            }
        }
    }

    /// <summary>
    /// Adds a message to the transcript in a thread-safe manner.
    /// </summary>
    public void AddUtterance(RealtimeConversationUtterance message)
    {
        lock (_lock)
        {
            _conversationHistory.Add(message);
        }

        LastModifiedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Adds a message to the transcript in a thread-safe manner.
    /// </summary>
    public void AddTranscriptMessage(ChatMessage message)
    {
        lock (_lock)
        {
            _transcript.Add(message);
        }

        LastModifiedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Adds multiple messages to the transcript in a thread-safe manner.
    /// </summary>
    public void AddTranscriptMessages(IEnumerable<ChatMessage> messages)
    {
        lock (_lock)
        {
            _transcript.AddRange(messages);
        }

        LastModifiedAt = DateTimeOffset.UtcNow;
    }
    /// <summary>
    /// Gets the unique identifier for this workflow state instance.
    /// </summary>
    public string WorkflowId => _workflowId ??= Guid.NewGuid().ToString();

    /// <summary>
    /// Gets the session identifier associated with this workflow.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the name of the currently executing step.
    /// </summary>
    public string? CurrentStepName { get; set; }


    /// <summary>
    /// Gets the index of the currently executing step.
    /// </summary>
    public int CurrentStepIndex { get; set; } = -1;
    public DateTimeOffset? StepStartedAt { get; set; }
    public string? CurrentPrompt { get; set; }

    /// <summary>
    /// Gets the time this workflow state was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;


    /// <summary>
    /// Gets the time this workflow state was last modified.
    /// </summary>
    public DateTimeOffset LastModifiedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the current status of the workflow.
    /// </summary>
    public IvrWorkflowStatus Status { get; set; } = IvrWorkflowStatus.NotStarted;

    public AuthenticationLevel AuthLevel { get; set; } = AuthenticationLevel.None;


    public bool IsComplete => Status is IvrWorkflowStatus.Completed or IvrWorkflowStatus.Failed or IvrWorkflowStatus.Cancelled;

    /// <summary>
    /// Gets or sets any error that occurred during workflow execution.
    /// </summary>
    public string? ErrorMessage { get; internal set; }


    /// <summary>
    /// Gets the number of retry attempts for the current step.
    /// </summary>
    public int CurrentStepRetryCount { get; internal set; }

    // Conversation metrics
    public int TotalTurns { get; set; }
    public double? SentimentScore { get; set; }
    public bool CustomerFrustrationDetected { get; set; }

    /// <summary>
    /// Gets a read-only view of completed step names.
    /// </summary>
    public IReadOnlyList<string> CompletedSteps
    {
        get
        {
            lock (_lock)
            {
                return [.. _completedSteps];
            }
        }
    }

    /// <summary>
    /// Sets a value in the workflow state.
    /// </summary>
    public void Set<T>(string key, T value)
    {
        _data[key] = value;
        _timestamps[key] = DateTimeOffset.UtcNow;
        LastModifiedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets a value from the workflow state.
    /// </summary>
    public T? Get<T>(string key)
    {
        if (_data.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }

        return default;
    }

    /// <summary>
    /// Tries to get a value from the workflow state.
    /// </summary>
    public bool TryGet<T>(string key, out T? value)
    {
        if (_data.TryGetValue(key, out var storedValue) && storedValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Checks if a key exists and has a value in the workflow state.
    /// </summary>
    public bool Has(string key)
    {
        return _data.TryGetValue(key, out var value) && value is not null;
    }

    /// <summary>
    /// Removes a value from the workflow state.
    /// </summary>
    public bool Remove(string key)
    {
        var removed = _data.TryRemove(key, out _);
        if (removed)
        {
            _timestamps.TryRemove(key, out _);
            LastModifiedAt = DateTimeOffset.UtcNow;
        }

        return removed;
    }

    /// <summary>
    /// Gets all keys in the workflow state.
    /// </summary>
    public IReadOnlyCollection<string> Keys => _data.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Gets the timestamp when a specific key was last set.
    /// </summary>
    public DateTimeOffset? GetTimestamp(string key)
    {
        return _timestamps.TryGetValue(key, out var timestamp) ? timestamp : null;
    }

    /// <summary>
    /// Marks a step as completed.
    /// </summary>
    public void MarkStepCompleted(string stepName)
    {
        lock (_lock)
        {
            if (!_completedSteps.Contains(stepName))
            {
                _completedSteps.Add(stepName);
            }
        }

        LastModifiedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Marks a step as completed.
    /// </summary>
    public void SetErrorMessage(string? errorMessage = null)
    {
        ErrorMessage = errorMessage;

        LastModifiedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Checks if a step has been completed.
    /// </summary>
    public bool IsStepCompleted(string stepName)
    {
        lock (_lock)
        {
            return _completedSteps.Contains(stepName);
        }
    }

    /// <summary>
    /// Creates a snapshot of the current state as a dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToSnapshot()
    {
        return new Dictionary<string, object?>(_data);
    }
}

/// <summary>
/// Represents the status of an IVR workflow.
/// </summary>
public enum IvrWorkflowStatus
{
    /// <summary>
    /// Workflow has not started yet.
    /// </summary>
    NotStarted,

    /// <summary>
    /// Workflow is currently running.
    /// </summary>
    Running,

    /// <summary>
    /// Workflow is waiting for user input.
    /// </summary>
    WaitingForInput,

    /// <summary>
    /// Workflow is waiting for user input.
    /// </summary>
    TransferRequested,

    /// <summary>
    /// Workflow completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Workflow failed with an error.
    /// </summary>
    Failed,

    /// <summary>
    /// Workflow was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Extension methods and helpers for IvrWorkflowState to support orchestrator scenarios.
/// </summary>
public static class IvrWorkflowStateExtensions
{
    // Well-known state keys for orchestrator scenarios
    public const string CustomerIdKey = "customerId";
    public const string CustomerNameKey = "customerName";
    public const string CustomerPhoneKey = "customerPhone";
    public const string AuthLevelKey = "authLevel";
    public const string PrimaryIntentKey = "primaryIntent";
    public const string DetectedIntentsKey = "detectedIntents";
    public const string FulfilledIntentsKey = "fulfilledIntents";
    public const string ConversationTurnsKey = "conversationTurns";
    public const string SentimentKey = "sentiment";
    public const string FrustrationLevelKey = "frustrationLevel";
    public const string StageHistoryKey = "stageHistory";
    public const string CurrentStageKey = "currentStage";
    public const string PreviousStageKey = "previousStage";

    /// <summary>
    /// Gets the customer ID from the workflow state.
    /// </summary>
    public static string? GetCustomerId(this IvrWorkflowState state)
    {
        return state.Get<string>(CustomerIdKey);
    }

    /// <summary>
    /// Sets the customer ID in the workflow state.
    /// </summary>
    public static void SetCustomerId(this IvrWorkflowState state, string customerId)
    {
        state.Set(CustomerIdKey, customerId);
    }

    /// <summary>
    /// Gets the customer name from the workflow state.
    /// </summary>
    public static string? GetCustomerName(this IvrWorkflowState state)
    {
        return state.Get<string>(CustomerNameKey);
    }

    /// <summary>
    /// Sets the customer name in the workflow state.
    /// </summary>
    public static void SetCustomerName(this IvrWorkflowState state, string name)
    {
        state.Set(CustomerNameKey, name);
    }

    /// <summary>
    /// Gets the customer phone number from the workflow state.
    /// </summary>
    public static string? GetCustomerPhone(this IvrWorkflowState state)
    {
        return state.Get<string>(CustomerPhoneKey);
    }

    /// <summary>
    /// Sets the customer phone number in the workflow state.
    /// </summary>
    public static void SetCustomerPhone(this IvrWorkflowState state, string phone)
    {
        state.Set(CustomerPhoneKey, phone);
    }

    /// <summary>
    /// Gets the authentication level from the workflow state.
    /// </summary>
    public static AuthenticationLevel GetAuthLevel(this IvrWorkflowState state)
    {
        return state.Get<AuthenticationLevel>(AuthLevelKey);
    }

    /// <summary>
    /// Sets the authentication level in the workflow state.
    /// </summary>
    public static void SetAuthLevel(this IvrWorkflowState state, AuthenticationLevel level)
    {
        state.Set(AuthLevelKey, level);
    }

    /// <summary>
    /// Gets the primary intent from the workflow state.
    /// </summary>
    public static string? GetPrimaryIntent(this IvrWorkflowState state)
    {
        return state.Get<string>(PrimaryIntentKey);
    }

    /// <summary>
    /// Sets the primary intent in the workflow state.
    /// </summary>
    public static void SetPrimaryIntent(this IvrWorkflowState state, string intent)
    {
        state.Set(PrimaryIntentKey, intent);
    }

    /// <summary>
    /// Gets the list of detected intents.
    /// </summary>
    public static List<string> GetDetectedIntents(this IvrWorkflowState state)
    {
        return state.Get<List<string>>(DetectedIntentsKey) ?? [];
    }

    /// <summary>
    /// Adds a detected intent.
    /// </summary>
    public static void AddDetectedIntent(this IvrWorkflowState state, string intent)
    {
        var intents = state.GetDetectedIntents();
        if (!intents.Contains(intent))
        {
            intents.Add(intent);
            state.Set(DetectedIntentsKey, intents);
        }
    }

    /// <summary>
    /// Gets the list of fulfilled intents.
    /// </summary>
    public static List<string> GetFulfilledIntents(this IvrWorkflowState state)
    {
        return state.Get<List<string>>(FulfilledIntentsKey) ?? [];
    }

    /// <summary>
    /// Marks an intent as fulfilled.
    /// </summary>
    public static void MarkIntentFulfilled(this IvrWorkflowState state, string intent)
    {
        var fulfilled = state.GetFulfilledIntents();
        if (!fulfilled.Contains(intent))
        {
            fulfilled.Add(intent);
            state.Set(FulfilledIntentsKey, fulfilled);
        }
    }

    /// <summary>
    /// Gets the conversation turn count.
    /// </summary>
    public static int GetConversationTurns(this IvrWorkflowState state)
    {
        return state.Get<int>(ConversationTurnsKey);
    }

    /// <summary>
    /// Increments the conversation turn count.
    /// </summary>
    public static void IncrementConversationTurns(this IvrWorkflowState state)
    {
        var turns = state.GetConversationTurns();
        state.Set(ConversationTurnsKey, turns + 1);
    }

    /// <summary>
    /// Gets the current sentiment score.
    /// </summary>
    public static double GetSentiment(this IvrWorkflowState state)
    {
        return state.Get<double>(SentimentKey);
    }

    /// <summary>
    /// Sets the sentiment score.
    /// </summary>
    public static void SetSentiment(this IvrWorkflowState state, double sentiment)
    {
        state.Set(SentimentKey, sentiment);
    }

    /// <summary>
    /// Gets the current frustration level.
    /// </summary>
    public static double GetFrustrationLevel(this IvrWorkflowState state)
    {
        return state.Get<double>(FrustrationLevelKey);
    }

    /// <summary>
    /// Sets the frustration level.
    /// </summary>
    public static void SetFrustrationLevel(this IvrWorkflowState state, double level)
    {
        state.Set(FrustrationLevelKey, level);
    }

    /// <summary>
    /// Gets the current stage.
    /// </summary>
    public static string? GetCurrentStage(this IvrWorkflowState state)
    {
        return state.Get<string>(CurrentStageKey);
    }

    /// <summary>
    /// Gets the previous stage.
    /// </summary>
    public static string? GetPreviousStage(this IvrWorkflowState state)
    {
        return state.Get<string>(PreviousStageKey);
    }
}
