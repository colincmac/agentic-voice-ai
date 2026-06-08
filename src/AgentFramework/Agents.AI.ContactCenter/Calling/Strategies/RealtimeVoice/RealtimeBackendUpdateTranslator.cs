using Extensions.AI.Contents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;

/// <summary>
/// Converts <see cref="AgentResponseUpdate"/> instances coming out of
/// <see cref="Realtime.RealtimeAIAgent.GetStreamingResponseAsync"/>
/// into <see cref="RealtimeBackendUpdate"/> records that the new
/// <see cref="IRealtimeVoiceBackend"/> contract exposes.
/// </summary>
/// <remarks>
/// Public + static so tests can pin down translation behavior without standing up
/// a realtime client. The adapter (<see cref="AuthorizingAIAgent"/>)
/// uses this exclusively.
/// </remarks>
public static class RealtimeBackendUpdateTranslator
{
    /// <summary>
    /// Translate one streaming update into zero or more <see cref="RealtimeBackendUpdate"/>s.
    /// Order is preserved.
    /// </summary>
    public static IEnumerable<RealtimeBackendUpdate> Translate(AgentResponseUpdate update)
    {
        var role = update.Role;
        var speakerLabel = role == ChatRole.User ? "user" : "assistant";
        var at = update.CreatedAt ?? DateTimeOffset.UtcNow;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case DataContent dc when !dc.Data.IsEmpty:
                    yield return new RealtimeBackendUpdate.Audio(dc.Data, at);
                    break;

                case AudioTranscriptionContent atc when !string.IsNullOrWhiteSpace(atc.Text):
                    // Realtime providers stream interim transcript fragments; mark non-final
                    // so observers know not to commit them as the user's final utterance.
                    yield return new RealtimeBackendUpdate.Transcript(
                        Speaker: speakerLabel,
                        Text: atc.Text,
                        IsFinal: false,
                        At: at);
                    break;

                case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                    if (role == ChatRole.User)
                    {
                        // Some providers surface a final user transcript as TextContent.
                        yield return new RealtimeBackendUpdate.Transcript(
                            Speaker: "user",
                            Text: tc.Text,
                            IsFinal: true,
                            At: at);
                    }
                    else
                    {
                        yield return new RealtimeBackendUpdate.AgentText(tc.Text, at);
                    }
                    break;

                case FunctionCallContent fcc:
                    yield return new RealtimeBackendUpdate.FunctionCalled(
                        Name: fcc.Name,
                        Arguments: fcc.Arguments is { } args
                            ? new Dictionary<string, object?>(args)
                            : new Dictionary<string, object?>(),
                        CallId: fcc.CallId,
                        At: at);
                    break;

                case FunctionResultContent frc:
                    yield return new RealtimeBackendUpdate.FunctionResult(
                        CallId: frc.CallId,
                        Result: frc.Result,
                        At: at);
                    break;

                case RealtimeVadContent vad:
                    // Surface caller speech-start so the strategy can barge-in
                    // (cancel any in-flight agent audio). Speech-ended is currently
                    // not actionable at the backend layer.
                    if (vad.VadEvent == VadEventType.InputSpeechStarted)
                    {
                        yield return new RealtimeBackendUpdate.UserSpeechStarted(at);
                    }
                    break;
            }
        }
    }
}
