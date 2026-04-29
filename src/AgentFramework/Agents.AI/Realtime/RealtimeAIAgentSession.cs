using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

/// <summary>
/// Provides a session implementation for use with <see cref="RealtimeAIAgent"/>.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class RealtimeAIAgentSession : AgentSession
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAIAgentSession"/> class.
    /// </summary>
    internal RealtimeAIAgentSession()
    {
    }

    [JsonConstructor]
    internal RealtimeAIAgentSession(string? sessionId, AgentSessionStateBag? stateBag) : base(stateBag ?? new())
    {
        this.RealtimeSessionId = sessionId;
    }

    /// <summary>
    /// Gets or sets the underlying realtime session identifier managed by the <see cref="IRealtimeClient"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property may be null if the session has not yet been initialized with the realtime service.
    /// Once a realtime session is created, this ID tracks the active session for reconnection and state management.
    /// </para>
    /// </remarks>
    [JsonPropertyName("sessionId")]
    public string? RealtimeSessionId
    {
        get;
        internal set
        {
            if (string.IsNullOrWhiteSpace(field) && string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the underlying <see cref="IRealtimeClientSession"/> associated with this agent session.
    /// </summary>
    /// <remarks>
    /// This holds the active realtime client session for sending and receiving messages.
    /// It is not serialized and must be re-established when restoring a session.
    /// </remarks>
    [JsonIgnore]
    public IRealtimeClientSession? ClientSession { get; internal set; }

    /// <summary>
    /// Creates a new instance of the <see cref="RealtimeAIAgentSession"/> class from previously serialized state.
    /// </summary>
    /// <param name="serializedState">A <see cref="JsonElement"/> representing the serialized state of the session.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization options to use instead of the default options.</param>
    /// <returns>The deserialized <see cref="RealtimeAIAgentSession"/>.</returns>
    internal static RealtimeAIAgentSession Deserialize(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (serializedState.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The serialized session state must be a JSON object.", nameof(serializedState));
        }

        var jso = jsonSerializerOptions ?? AgentsJsonContext.DefaultOptions;
        return serializedState.Deserialize(jso.GetTypeInfo(typeof(RealtimeAIAgentSession))) as RealtimeAIAgentSession
            ?? new RealtimeAIAgentSession();
    }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var jso = jsonSerializerOptions ?? AgentsJsonContext.DefaultOptions;
        return JsonSerializer.SerializeToElement(this, jso.GetTypeInfo(typeof(RealtimeAIAgentSession)));
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay =>
        this.RealtimeSessionId is { } sessionId
            ? $"RealtimeSessionId = {sessionId}, StateBag Count = {this.StateBag.Count}"
            : $"StateBag Count = {this.StateBag.Count}";
}
