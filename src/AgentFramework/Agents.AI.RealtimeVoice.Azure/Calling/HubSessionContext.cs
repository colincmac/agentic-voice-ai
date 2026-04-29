using Azure.Communication.CallAutomation;
using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.RealtimeVoice.Azure.Calling;


/// <summary>
/// Lightweight session-scoped context that provides access to the session's
/// <see cref="IServiceProvider"/> for on-demand service resolution.
/// Replaces the previous god-object pattern of eagerly resolving all services.
/// </summary>
public sealed class HubSessionContext
{
    private readonly IServiceScope _sessionScope;
    private readonly ConversationContext _conversationContext;

    /// <summary>
    /// Creates a HubSessionContext 
    /// </summary>
    public HubSessionContext(string sessionId, IServiceScope sessionScope)
    {
        SessionId = sessionId;
        _sessionScope = sessionScope;
        _conversationContext = sessionScope.ServiceProvider.GetService<ConversationContext>() ?? new ConversationContext();
    }

    public string SessionId { get; }

    /// <summary>
    /// Structured pinned conversation memory for the current session.
    /// </summary>
    public ConversationContext ConversationContext => _conversationContext;

    /// <summary>
    /// The session-scoped service provider for on-demand service resolution.
    /// </summary>
    public IServiceProvider SessionServices => _sessionScope.ServiceProvider;

    /// <summary>
    /// Convenience accessor for <see cref="CallAutomationClient"/> from the session scope.
    /// </summary>
    public CallAutomationClient CallAutomation => _sessionScope.ServiceProvider.GetRequiredService<CallAutomationClient>();

    /// <summary>
    /// Resolves a service from the session scope.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull
        => typeof(T) == typeof(ConversationContext)
            ? (T)(object)_conversationContext
            : _sessionScope.ServiceProvider.GetRequiredService<T>();

    /// <summary>
    /// Resolves an optional service from the session scope.
    /// </summary>
    public T? GetService<T>() where T : class
        => typeof(T) == typeof(ConversationContext)
            ? (T)(object)_conversationContext
            : _sessionScope.ServiceProvider.GetService<T>();
}
