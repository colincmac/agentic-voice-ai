using Azure.Communication.CallAutomation;

namespace Agents.AI.RealtimeVoice.Azure.Calling.Proposed.Implementation;

/// <summary>
/// Tiny adapter over the bits of <see cref="CallMedia"/> that
/// <see cref="AcsCallAutomationEdge"/> uses. Exists so tests can drive the verb
/// edge without standing up a real ACS call connection.
/// </summary>
public interface ICallMediaClient
{
    Task PlayTextAsync(string text, string? voiceName, string? operationContext, CancellationToken cancellationToken);
    Task PlayFileAsync(Uri fileUri, string? operationContext, CancellationToken cancellationToken);
    Task CancelAllAsync(CancellationToken cancellationToken);
    Task RecognizeDtmfAsync(int maxTones, char? stopTone, TimeSpan? interToneTimeout, TimeSpan? initialSilenceTimeout, string? operationContext, CancellationToken cancellationToken);
}

/// <summary>
/// Production adapter that forwards directly to <see cref="CallMedia"/> on a real
/// <see cref="CallConnection"/>. The recognize verb targets the caller leg via the
/// supplied <see cref="Azure.Communication.CommunicationIdentifier"/>.
/// </summary>
public sealed class CallMediaClient : ICallMediaClient
{
    private readonly CallMedia _media;
    private readonly global::Azure.Communication.CommunicationIdentifier _target;

    public CallMediaClient(CallConnection connection, global::Azure.Communication.CommunicationIdentifier targetParticipant)
    {
        _media = connection.GetCallMedia();
        _target = targetParticipant;
    }

    public Task PlayTextAsync(string text, string? voiceName, string? operationContext, CancellationToken cancellationToken)
    {
        var source = string.IsNullOrEmpty(voiceName)
            ? new TextSource(text)
            : new TextSource(text, voiceName);
        var options = new PlayToAllOptions(source) { OperationContext = operationContext };
        return _media.PlayToAllAsync(options, cancellationToken);
    }

    public Task PlayFileAsync(Uri fileUri, string? operationContext, CancellationToken cancellationToken)
    {
        var source = new FileSource(fileUri);
        var options = new PlayToAllOptions(source) { OperationContext = operationContext };
        return _media.PlayToAllAsync(options, cancellationToken);
    }

    public Task CancelAllAsync(CancellationToken cancellationToken)
        => _media.CancelAllMediaOperationsAsync(cancellationToken);

    public Task RecognizeDtmfAsync(
        int maxTones,
        char? stopTone,
        TimeSpan? interToneTimeout,
        TimeSpan? initialSilenceTimeout,
        string? operationContext,
        CancellationToken cancellationToken)
    {
        var options = new CallMediaRecognizeDtmfOptions(_target, maxTones)
        {
            OperationContext = operationContext,
        };
        if (stopTone is char stop)
        {
            options.StopTones.Add(MapStopTone(stop));
        }
        if (interToneTimeout is TimeSpan it)
        {
            options.InterToneTimeout = it;
        }
        if (initialSilenceTimeout is TimeSpan ist)
        {
            options.InitialSilenceTimeout = ist;
        }
        return _media.StartRecognizingAsync(options, cancellationToken);
    }

    private static global::Azure.Communication.CallAutomation.DtmfTone MapStopTone(char digit) => digit switch
    {
        '0' => global::Azure.Communication.CallAutomation.DtmfTone.Zero,
        '1' => global::Azure.Communication.CallAutomation.DtmfTone.One,
        '2' => global::Azure.Communication.CallAutomation.DtmfTone.Two,
        '3' => global::Azure.Communication.CallAutomation.DtmfTone.Three,
        '4' => global::Azure.Communication.CallAutomation.DtmfTone.Four,
        '5' => global::Azure.Communication.CallAutomation.DtmfTone.Five,
        '6' => global::Azure.Communication.CallAutomation.DtmfTone.Six,
        '7' => global::Azure.Communication.CallAutomation.DtmfTone.Seven,
        '8' => global::Azure.Communication.CallAutomation.DtmfTone.Eight,
        '9' => global::Azure.Communication.CallAutomation.DtmfTone.Nine,
        '*' => global::Azure.Communication.CallAutomation.DtmfTone.Asterisk,
        '#' => global::Azure.Communication.CallAutomation.DtmfTone.Pound,
        'A' or 'a' => global::Azure.Communication.CallAutomation.DtmfTone.A,
        'B' or 'b' => global::Azure.Communication.CallAutomation.DtmfTone.B,
        'C' or 'c' => global::Azure.Communication.CallAutomation.DtmfTone.C,
        'D' or 'd' => global::Azure.Communication.CallAutomation.DtmfTone.D,
        _ => throw new ArgumentOutOfRangeException(nameof(digit), digit, "Unsupported DTMF stop tone")
    };
}
