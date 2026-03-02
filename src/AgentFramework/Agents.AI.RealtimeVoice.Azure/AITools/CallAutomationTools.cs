
//using System.ComponentModel;
//using Agents.AI.Extensions.AITools;
//using Agents.AI.Extensions.AITools.ToolApproval.VoiceApproval;
//using Agents.AI.Extensions.Helpers.Streaming;
//using Agents.AI.Extensions.Voice;
//using Agents.AI.RealtimeVoice.Azure.Calling.AIContext;
//using Agents.AI.RealtimeVoice.Azure.Calling.Models;
//using Agents.AI.RealtimeVoice.Azure.Calling.Transports;
//using Agents.AI.RealtimeVoice.Azure.Configuration;
//using Azure;
//using Azure.Communication;
//using Azure.Communication.CallAutomation;
//using DnsClient.Internal;
//using Microsoft.Extensions.AI;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Abstractions;

//namespace Agents.AI.RealtimeVoice.Azure.Calling.AITools;

//public class ScopedCallAutomationTools(
//    CallAutomationClient callAutomationClient,
//    CallConnectionInfo callConnectionInfo,
//    ContactCenterConversationSession session,
//    string agentParticipantId,
//    ILogger<ScopedCallAutomationTools>? logger = null)
//{
//    private readonly CallAutomationClient _callAutomationClient = callAutomationClient;
//    private readonly CallConnectionInfo _callConnectionInfo = callConnectionInfo;
//    private readonly ContactCenterConversationSession _session = session;
//    private readonly string _agentParticipantId = agentParticipantId;
//    private readonly ILogger<ScopedCallAutomationTools> _logger = logger ?? NullLogger<ScopedCallAutomationTools>.Instance;

//    public TeamsCallQueue teamsCallQueue => new TeamsCallQueue
//    {
//        ResourceObjectId = "dcb72e8f-ac22-4ef6-99e6-f66252f532a7",
//        ResourceAccountPhoneNumber = "+16469050715"
//    };
//    private readonly CommunicationOptions _options = new CommunicationOptions()
//    {
//        Acs = new ()
//        {
//            ConnectionString = "",
//            CallBackUri = new Uri("https://example.com/callback"),
//            MediaStreamingUri = new Uri("https://example.com/mediastream")
//        },
//        Teams = new ()
//        {
//            ResourceTenantId = "",
//            ResourceObjectId = "",
//            CallQueues = [
//                new ()
//                {
//                    ResourceObjectId = "dcb72e8f-ac22-4ef6-99e6-f66252f532a7",
//                    ResourceAccountPhoneNumber = "dcb72e8f-ac22-4ef6-99e6-f66252f532a7"
//                }
//            ]
//        },
        
//    };
//    public IEnumerable<AITool> AsAITools()
//    {
//        yield return AIFunctionFactory.Create(TransferCallAsync);
//        yield return AIFunctionFactory.Create(HangUpCallAsync);
//        yield return AIFunctionFactory.Create(PutOnHoldAsync);
//        yield return AIFunctionFactory.Create(ResumeFromHoldAsync);
//    }

//    private readonly TeamsExtensionUserIdentifier _teamsApp = new("dcb72e8f-ac22-4ef6-99e6-f66252f532a7", "018c93ad-d06d-46ed-a5ac-a5be9d89a031", "");
//    private readonly MicrosoftTeamsAppIdentifier _teamsQueue = new("dcb72e8f-ac22-4ef6-99e6-f66252f532a7");
//    private readonly PhoneNumberIdentifier _teamsQueuePhone = new("dcb72e8f-ac22-4ef6-99e6-f66252f532a7");


//    [Description("Put the current call on hold with optional hold music")]
//    [DisplayName("Put On Hold")]
//    [VoiceTool]
//    public async Task<object> PutOnHoldAsync(
//        [Description("Reason for putting the call on hold")] string reason,
//        CancellationToken ct = default)
//    {
//        var callerChannel = _session.Transports.Values
//            .FirstOrDefault(c => c.Metadata.ChannelType == CommunicationChannelType.Phone);

//        if (callerChannel?.Metadata.CallConnectionId == null)
//        {
//            return new { success = false, error = "No active call to put on hold" };
//        }

//        // Mark channel as on hold
//        callerChannel.Metadata.IsOnHold = true;

//        var callConnection = _callAutomationClient.GetCallConnection(callerChannel.Metadata.CallConnectionId);

//        // Play hold music
//        var holdMusicSource = new FileSource(new Uri("https://example.com/hold-music.wav"));
//        await callConnection.GetCallMedia().PlayToAllAsync(
//            new PlayToAllOptions(holdMusicSource)
//            {
//                Loop = true,
//                OperationContext = $"hold:{reason}"
//            }, ct);

//        return new { success = true, message = "Call placed on hold" };
//    }

//    [Description("Resume a call that was previously put on hold")]
//    [DisplayName("Resume From Hold")]
//    [VoiceTool]
//    public async Task<object> ResumeFromHoldAsync(CancellationToken ct = default)
//    {
//        var callerChannel = _session.Transports.Values
//            .FirstOrDefault(c => c.Metadata.ChannelType == CommunicationChannelType.Phone
//                              && c.Metadata.IsOnHold);

//        if (callerChannel?.Metadata.CallConnectionId == null)
//        {
//            return new { success = false, error = "No call on hold to resume" };
//        }

//        callerChannel.Metadata.IsOnHold = false;

//        var callConnection = _callAutomationClient.GetCallConnection(callerChannel.Metadata.CallConnectionId);
//        await callConnection.GetCallMedia().CancelAllMediaOperationsAsync(ct);

//        return new { success = true, message = "Call resumed from hold" };
//    }

//    [Description("End the current call")]
//    [DisplayName("Hang Up Call")]
//    [VoiceTool]
//    public async Task<Response> HangUpCallAsync(
//        [Description("Whether to end the call for all participants")] bool hangupForEveryone,
//        CancellationToken ct = default)
//    {
//        var callerChannel = _session.Transports.Values
//            .FirstOrDefault(c => c.Metadata.ChannelType == CommunicationChannelType.Phone);

//        if (callerChannel?.Metadata.CallConnectionId == null)
//        {
//            throw new InvalidOperationException("No active call to hang up");
//        }

//        var callConnection = _callAutomationClient.GetCallConnection(callerChannel.Metadata.CallConnectionId);
//        return await callConnection.HangUpAsync(hangupForEveryone, ct);
//    }



//    [Description("Transfer the current call to a call queue for a specific department")]
//    [DisplayName("Transfer Call to Call Queue")]
//    [VoiceTool]
//    public async Task<object> TransferCallAsync(
//        [Description("A brief description of why the call needs to be transferred")] string transferReason,
//        [Description("A summary of the conversation so far")] string conversationSummary,
//        [Description("The type of agent to transfer to (e.g., 'billing', 'technical', 'supervisor')")] string? departmentType = null,
//        CancellationToken ct = default)
//    {
//        try
//        {
//            // Get the current caller's ACS channel
//            var callerChannel = _session.Transports.Values
//                .FirstOrDefault(c => c.Metadata.ChannelType == CommunicationChannelType.Phone);

//            if (callerChannel?.Metadata.CallConnectionId == null)
//            {
//                return new { success = false, error = "No active call to transfer" };
//            }

//            // Prepare the session for transfer
//            var transferMetadata = new TransferMetadata
//            {
//                Reason = transferReason,
//                Summary = conversationSummary,
//                OriginalCallId = callerChannel.Metadata.ServerCallId,
//                AgentParticipantId = _agentParticipantId,
//                Timestamp = DateTimeOffset.UtcNow
//            };

//            // Store transfer metadata in session for continuity
//            await _session.SetTransferMetadataAsync(transferMetadata, ct);

//            // Determine transfer target based on department type
//            var transferTarget = GetTransferTarget(departmentType);

//            // Perform the actual transfer
//            var callConnection = _callAutomationClient.GetCallConnection(callerChannel.Metadata.CallConnectionId);
//            var transferResult = await callConnection.TransferCallToParticipantAsync(
//                new TransferToParticipantOptions(new PhoneNumberIdentifier(""))
//                {
//                    OperationContext = conversationSummary
//                }, ct);

//            _logger.LogInformation(
//                "Initiated call transfer for session {SessionId} to {Department}",
//                _session.SessionId,
//                departmentType ?? "default queue");

//            return new
//            {
//                success = true,
//                message = "Call transfer initiated successfully",
//                transferId = transferResult.Value.OperationContext,
//                targetDepartment = departmentType
//            };
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Failed to transfer call in session {SessionId}", _session.SessionId);
//            return new { success = false, error = ex.Message };
//        }
//    }
   
//    // -------- Participants --------

//    [VoiceTool]
//    [Description("Adds a participant to the current call. Use when you want to escalate the call to a human agent.")]
//    public async Task<object> AddParticipantAsync([Description("A short description of the callers intent for the call.")] string intentOfUser, [Description("A summary of the call so far.")] string callSummary, CancellationToken ct = default)
//    {
//        var result = await GetCurrentCallConnection().TransferCallToParticipantAsync(new TransferToParticipantOptions(_teamsQueue), ct).ConfigureAwait(false);
//        //var result = await GetCurrentCallConnection().AddParticipantAsync(new CallInvite(), ct).ConfigureAwait(false);
//        //var newCall = await _callAutomationClient.CreateCallAsync(
//        //    new CreateCallOptions(
//        //        new CallInvite(_teamsQueuePhone),
//        //        new Uri(_options.Acs.CallBackUri, $"/calling/automation/callbacks/{_callConnectionInfo.CallConnectionId}"))
//        //    {
//        //        Subject = intentOfUser,
//        //        OperationContext = callSummary
//        //    }, ct).ConfigureAwait(false);
//        return result;
//    }



//    //[AgentTool(name: "lookup_phone_number", description: "Look up a phone number in the directory")]
//    //[VoiceTool]
//    //public async Task<object> SearchPhoneDirectory(string participantRawId, string? operationContext = null, CancellationToken ct = default)
//    //{
//    //    var participant = new CommunicationUserIdentifier(participantRawId);
//    //    var result = await GetCurrentCallConnection().AddParticipantAsync(new AddParticipantOptions(participant)
//    //    {
//    //        OperationContext = operationContext
//    //    }, ct).ConfigureAwait(false);

//    //    return new
//    //    {
//    //        participantRawId,
//    //        operationContext = result.OperationContext,
//    //        invitationId = result.InvitationId
//    //    };
//    //}

//    //[AgentTool(name: "cancel_add_participant", description: "Cancel a pending add-participant operation")]
//    //[VoiceTool]
//    //public Task CancelAddParticipantAsync(string invitationId, CancellationToken ct = default)
//    //    => GetCurrentCallConnection().CancelAddParticipantOperationAsync(invitationId, ct: ct);

//    //[AgentTool(name: "remove_participant", description: "Remove a participant from the call")]
//    //[VoiceTool]
//    //public Task RemoveParticipantAsync(string participantRawId, CancellationToken ct = default)
//    //{
//    //    var participant = new CommunicationUserIdentifier(participantRawId);
//    //    return GetCurrentCallConnection().RemoveParticipantAsync(participant, ct);
//    //}

//    //[AgentTool(name: "participant_list", description: "Get all participants")]
//    //public async Task<object> ListParticipantsAsync(CancellationToken ct = default)
//    //{
//    //    var result = await GetCurrentCallConnection().GetParticipantsAsync(ct).ConfigureAwait(false);
//    //    return result.Value.Select(p => new
//    //    {
//    //        rawId = p.Identifier.RawId,
//    //        isMuted = p.IsMuted
//    //    }).ToList();
//    //}

//    //[AgentTool(name: "participant_info", description: "Get info for a single participant")]
//    //public async Task<object?> GetParticipantAsync(string participantRawId, CancellationToken ct = default)
//    //{
//    //    var id = new CommunicationUserIdentifier(participantRawId);
//    //    var result = await GetCurrentCallConnection().GetParticipantAsync(id, ct).ConfigureAwait(false);
//    //    var p = result.Value;
//    //    return new
//    //    {
//    //        rawId = p.Identifier.RawId,
//    //        p.IsMuted
//    //    };
//    //}

//    //[AgentTool(name: "transfer_call_to_queue", description: "Transfer call to a single participant target")]
//    //[VoiceTool]
//    //public Task TransferCallAsync(string calls, string? operationContext = null, CancellationToken ct = default)
//    //{
//    //    var target = new CommunicationUserIdentifier(targetRawId);
//    //    return GetCurrentCallConnection().TransferCallToParticipantAsync(
//    //        new TransferToParticipantOptions(target)
//    //        {
//    //            OperationContext = operationContext
//    //        }, ct);
//    //}

//    //[AgentTool(name: "move_participant", description: "Move a participant from this call to another call (serverCallId target)")]
//    //[VoiceTool]
//    //public Task MoveParticipantAsync(string participantRawId, string targetCallConnectionId, CancellationToken ct = default)
//    //{
//    //    // ACS currently supports 'transfer' rather than an atomic move; implement as transfer.
//    //    var participant = new CommunicationUserIdentifier(participantRawId);
//    //    return GetCurrentCallConnection().TransferCallToParticipantAsync(new TransferToParticipantOptions(participant)
//    //    {
//    //        // For true 'move', business logic (notify other call, etc.) can be added externally.
//    //        OperationContext = $"move-to:{targetCallConnectionId}"
//    //    }, ct);
//    //}

//    //// -------- Media / Play / DTMF / Hold --------

//    //[AgentTool(name: "play_audio_file", description: "Play an audio file (URI) to all participants")]
//    //[VoiceTool]
//    //public Task PlayFileAsync(string fileUri, string? operationContext = null, CancellationToken ct = default)
//    //{
//    //    var source = new FileSource(new Uri(fileUri));
//    //    return GetMedia().PlayToAllAsync(new PlayToAllOptions(source)
//    //    {
//    //        OperationContext = operationContext
//    //    }, ct);
//    //}

//    //[AgentTool(name: "play_tts", description: "Play text-to-speech to all participants")]
//    //[VoiceTool]
//    //public Task PlayTtsAsync(string text, string voiceName = "en-US-JennyNeural", string? operationContext = null, CancellationToken ct = default)
//    //{
//    //    var source = new TextSource(text)
//    //    {
//    //        VoiceName = voiceName
//    //    };
//    //    return GetMedia().PlayToAllAsync(new PlayToAllOptions(source)
//    //    {
//    //        OperationContext = operationContext
//    //    }, ct);
//    //}

//    //[AgentTool(name: "play_ssml", description: "Play SSML to all participants")]
//    //[VoiceTool]
//    //public Task PlaySsmlAsync(string ssml, string? operationContext = null, CancellationToken ct = default)
//    //{
//    //    var source = new SsmlSource(ssml);
//    //    return GetMedia().PlayToAllAsync(new PlayToAllOptions(source)
//    //    {
//    //        OperationContext = operationContext
//    //    }, ct);
//    //}

//    //[AgentTool(name: "send_dtmf", description: "Send a sequence of DTMF tones to the call")]
//    //[VoiceTool]
//    //public Task SendDtmfAsync([Description("The DTMF tones to send (e.g. `123#`)")] string dtmfToneList, [Description("The DTMF tones to send (e.g. `123#`)")] string participantRawId, CancellationToken ct = default)
//    //{
//    //    var parsed = dtmfToneList
//    //        .Where(c => char.IsDigit(c) || c is '*' or '#')
//    //        .Select(MapTone)
//    //        .ToList();
//    //    return GetMedia().SendDtmfTonesAsync(new SendDtmfTonesOptions(parsed), ct);
//    //}

//    //private static DtmfTone MapTone(char c) => c switch
//    //{
//    //    '0' => DtmfTone.Zero,
//    //    '1' => DtmfTone.One,
//    //    '2' => DtmfTone.Two,
//    //    '3' => DtmfTone.Three,
//    //    '4' => DtmfTone.Four,
//    //    '5' => DtmfTone.Five,
//    //    '6' => DtmfTone.Six,
//    //    '7' => DtmfTone.Seven,
//    //    '8' => DtmfTone.Eight,
//    //    '9' => DtmfTone.Nine,
//    //    '*' => DtmfTone.Asterisk,
//    //    '#' => DtmfTone.Pound,
//    //    _ => throw new ArgumentOutOfRangeException(nameof(c))
//    //};

//    //[AgentTool(name: "hold_participant", description: "Start hold music for a participant (simulated hold)")]
//    //[VoiceTool]
//    //public Task HoldParticipantAsync(string participantRawId, string holdAudioFileUrl, CancellationToken ct = default)
//    //{
//    //    var target = new CommunicationUserIdentifier(participantRawId);
//    //    var source = new FileSource(new Uri(holdAudioFileUrl));
//    //    return GetMedia().StartHoldMusicAsync(target, source, ct: ct);
//    //}

//    //[AgentTool(name: "unhold_participant", description: "Stop hold music for a participant")]
//    //[VoiceTool]
//    //public Task UnholdParticipantAsync(string participantRawId, CancellationToken ct = default)
//    //{
//    //    var target = new CommunicationUserIdentifier(participantRawId);
//    //    return GetMedia().StopHoldMusicAsync(target, ct: ct);
//    //}

//    //// -------- Recording --------

//    //[AgentTool(name: "start_recording", description: "Start call recording")]
//    //[VoiceTool]
//    //public async Task<object> StartRecordingAsync(bool audioOnly = true, CancellationToken ct = default)
//    //{
//    //    var recordingClient = _callAutomationClient.GetCallRecording();
//    //    var options = new StartRecordingOptions(new ServerCallLocator(_callConnectionInfo.CallConnectionId))
//    //    {
//    //        RecordingChannel = audioOnly ? RecordingChannel.Unmixed : RecordingChannel.Mixed
//    //    };
//    //    var result = await recordingClient.StartAsync(options, ct).ConfigureAwait(false);
//    //    return new { recordingId = result.Value.RecordingId };
//    //}

//    //[AgentTool(name: "stop_recording", description: "Stop call recording by recordingId")]
//    //[VoiceTool]
//    //public Task StopRecordingAsync(string recordingId, CancellationToken ct = default)
//    //    => _callAutomationClient.GetCallRecording().StopAsync(recordingId, ct);

//    //// -------- Transcription (may be preview; guard) --------

//    //[AgentTool(name: "start_transcription", description: "Start transcription (if supported)")]
//    //[VoiceTool]
//    //public Task StartTranscriptionAsync(CancellationToken ct = default)
//    //{
//    //    // Placeholder: implement when SDK exposes transcription API.
//    //    return Task.CompletedTask;
//    //}

//    //[AgentTool(name: "stop_transcription", description: "Stop transcription (if supported)")]
//    //[VoiceTool]
//    //public Task StopTranscriptionAsync(CancellationToken ct = default)
//    //    => Task.CompletedTask;

//    //// -------- Properties / Diagnostics --------

//    //[AgentTool(name: "call_properties", description: "Get current call connection properties")]
//    //public async Task<object> GetCallPropertiesAsync(CancellationToken ct = default)
//    //{
//    //    var callConn = GetCurrentCallConnection();
//    //    var props = await GetCurrentCallConnection().GetCallConnectionPropertiesAsync(ct).ConfigureAwait(false);

//    //    return new
//    //    {
//    //        props.Value.CallConnectionId,
//    //        props.Value.ServerCallId,
//    //        props.Value.CorrelationId,
//    //        props.Value.CallConnectionState
//    //    };
//    //}

//    // Utility
//    private async Task InjectTransferContextToAgentAsync(
//    RealtimeAIAgentTransport agentChannel,
//    TransferMetadata transferMetadata,
//    CancellationToken cancellationToken)
//    {
//        // Send a system message to the agent about the transfer
//        var systemMessage = new MessageUpdate
//        {
//            CreatedAt = DateTimeOffset.UtcNow,
//            Contents = [
//                new TextContent($"[SYSTEM] Call transferred successfully. Previous summary: {transferMetadata.Summary}")
//            ],
//            SenderParticipantId = "system"
//        };

//        await agentChannel.SendMessageAsync(systemMessage, cancellationToken);
//    }

//    /// <summary>
//    private CallConnection GetCurrentCallConnection() => _callAutomationClient.GetCallConnection(_callConnectionInfo.CallConnectionId);
//    private CallMedia GetMedia() => GetCurrentCallConnection().GetCallMedia();
//    private CommunicationIdentifier GetTransferTarget(string? departmentType)
//    {
//        // This would be configured based on your routing rules
//        return departmentType?.ToLower() switch
//        {
//            "billing" => new PhoneNumberIdentifier(""),
//            "technical" => new PhoneNumberIdentifier("+0987654321"),
//            "supervisor" => new MicrosoftTeamsAppIdentifier("supervisor-queue-id"),
//            _ => new MicrosoftTeamsAppIdentifier("default-queue-id")
//        };
//    }
//}

//public interface ICallAutomation
//{
//    Task<Response> HangUpAsync(CancellationToken ct = default);
//}

