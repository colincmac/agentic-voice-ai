namespace Agents.AI.RealtimeVoice.Azure.Media.Signaling;

/// <summary>
/// A transport that receives inbound control signals (DTMF, call-control) and
/// forwards them into the session via a registered callback.
/// </summary>
public interface ISignalConsumer
{
    /// <summary>
    /// Register an inbound signal handler invoked for every received control signal.
    /// </summary>
    void SetOnSignalReceived(Func<string, SessionSignal, CancellationToken, Task> handler);
}

/// <summary>
/// Lightweight envelope for transport-level control signals such as DTMF tones,
/// hold/transfer requests, or VAD-originated events.
/// </summary>
public sealed class SessionSignal
{
    public required SessionSignalKind Kind { get; init; }

    /// <summary>Optional payload; interpretation depends on <see cref="Kind"/>.</summary>
    public string? Value { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public enum SessionSignalKind
{
    /// <summary>A DTMF tone (value = tone character, e.g. "5", "#").</summary>
    Dtmf,

    /// <summary>Request to place the channel on hold.</summary>
    Hold,

    /// <summary>Request to resume from hold.</summary>
    Resume,

    /// <summary>Request to transfer to another destination (value = target).</summary>
    Transfer,

    /// <summary>Mute toggle (value = "true" | "false").</summary>
    Mute,

    /// <summary>Custom application-defined signal.</summary>
    Custom
}
