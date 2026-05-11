namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed;

// Discriminated union of things a strategy can ask the edge to do.
//
// Two distinct shapes of edge consume these:
//
//  - Streaming edges (ACS bidirectional media WebSocket, browser SignalR audio):
//    handle Audio + StopPlayback. They reject SpeakText / PlayFile / CollectDtmf
//    because they have no platform to delegate to — the strategy must produce
//    raw PCM itself when paired with a streaming edge.
//
//  - Verb-based edges (ACS Call Automation REST without media WS):
//    handle SpeakText (TextSource via attached Cognitive Services), PlayFile
//    (FileSource), StopPlayback (CancelAllMediaOperations), and CollectDtmf
//    (StartRecognizing). They reject Audio because there's no place to write
//    bytes — ACS does the rendering.
//
// Strategies and edges are paired explicitly. We don't auto-synthesize on a
// streaming edge, and we don't try to encode PCM on a verb-based edge. If a
// strategy emits a directive its edge can't dispatch, the edge logs and drops
// it — surfaced via DispatchUnsupported in StrategyEvent.

/// <summary>
/// One thing a strategy wants the edge to do at this moment.
/// </summary>
public abstract record OutboundDirective(DateTimeOffset At)
{
    /// <summary>Raw PCM frame for streaming edges.</summary>
    public sealed record Audio(AudioFrame Frame) : OutboundDirective(Frame.Timestamp);

    /// <summary>Have the platform synthesize and play <paramref name="Text"/>. Verb-based edges only.</summary>
    public sealed record SpeakText(
        string Text,
        DateTimeOffset At,
        string? VoiceName = null,
        string? OperationContext = null) : OutboundDirective(At);

    /// <summary>Have the platform play a hosted audio file. Verb-based edges only.</summary>
    public sealed record PlayFile(
        Uri FileUri,
        DateTimeOffset At,
        string? OperationContext = null) : OutboundDirective(At);

    /// <summary>Cancel any in-flight playback (caller barge-in, supervisor takeover).</summary>
    public sealed record StopPlayback(DateTimeOffset At) : OutboundDirective(At);

    /// <summary>
    /// Ask the platform to recognize DTMF from the caller and surface the digits
    /// on InboundDtmf. Verb-based edges only — streaming edges receive DTMF events
    /// inline on the WebSocket as they arrive and this directive is a no-op there.
    /// </summary>
    public sealed record CollectDtmf(
        int MaxTones,
        DateTimeOffset At,
        char? StopTone = null,
        TimeSpan? InterToneTimeout = null,
        TimeSpan? InitialSilenceTimeout = null,
        string? OperationContext = null) : OutboundDirective(At);
}
