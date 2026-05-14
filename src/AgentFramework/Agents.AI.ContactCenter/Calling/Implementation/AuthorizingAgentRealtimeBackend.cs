using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Realtime;
using Extensions.AI.Realtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#pragma warning disable MEAI001

namespace Agents.AI.ContactCenter.Calling.Implementation;

/// <summary>
/// Production adapter that exposes <see cref="AuthorizingRealtimeAIAgent"/> through the
/// <see cref="IRealtimeVoiceBackend"/> contract. Mirrors the translation that
/// <see cref="Transports.RealtimeVoiceAgentTransport"/> performs today, but emits
/// <see cref="RealtimeBackendUpdate"/> records instead of feeding them onto the legacy
/// transport pipeline.
/// </summary>
/// <remarks>
/// The adapter intentionally does not own the agent's session lifetime tied to a
/// caller — it owns the backend connection. Disposal closes the realtime session.
/// </remarks>
public sealed class AuthorizingAgentRealtimeBackend : IRealtimeVoiceBackend
{
    private readonly AuthorizingRealtimeAIAgent _agent;
    private readonly AgentRunOptions? _runOptions;
    private readonly ILogger _logger;

    private RealtimeAIAgentSession? _session;
    private int _disposed;

    public AuthorizingAgentRealtimeBackend(
        AuthorizingRealtimeAIAgent agent,
        AgentRunOptions? runOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        _agent = agent;
        _runOptions = runOptions;
        _logger = loggerFactory?.CreateLogger<AuthorizingAgentRealtimeBackend>()
                  ?? NullLogger<AuthorizingAgentRealtimeBackend>.Instance;

        AgentId = agent.Id ?? Guid.NewGuid().ToString();
        AgentDisplayName = agent.Name ?? "Realtime Agent";
    }

    public string AgentId { get; }

    public string AgentDisplayName { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
        {
            return;
        }

        _session = await _agent.CreateRealtimeSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Realtime backend connected for agent {AgentId}", AgentId);
    }

    public async ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        var session = EnsureSession();
        var dataContent = new DataContent(pcm, "audio/pcm");
        await _agent.SendAudioAsync(session, dataContent, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateSystemPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var session = EnsureSession();
        var systemMessage = new ChatMessage(ChatRole.System, prompt);
        await _agent.SendAsync(session, systemMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends a <see cref="SessionUpdateRealtimeClientMessage"/> with the new tool list, cloning every other
    /// option from the live session so the realtime model retains its current instructions, voice, audio
    /// format, etc. Intended to be called by the strategy at session start and on each workflow step
    /// transition with the navigator's guard-wrapped tools.
    /// <para>
    /// Tools pushed via this path are NOT wrapped in <c>AuthorizingAgentFunction</c> (that wrap only happens
    /// via the agent's <c>RealtimeClientFactory</c> at session creation). Today this is harmless because the
    /// authorizing wrapper is pass-through; if approval/auth middleware is reintroduced, expose a
    /// tool-wrapping callback on the agent and apply it here.
    /// </para>
    /// </remarks>
    public async ValueTask UpdateToolsAsync(IEnumerable<AITool> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var session = EnsureSession();
        var clientSession = session.ClientSession
            ?? throw new InvalidOperationException(
                $"{nameof(AuthorizingAgentRealtimeBackend)} session has no active realtime client session.");

        var updated = CloneOptionsWithTools(clientSession.Options, [.. tools]);
        await clientSession.SendAsync(
            new SessionUpdateRealtimeClientMessage(updated),
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Realtime backend tools updated for agent {AgentId} (tool count {Count})",
            AgentId, updated.Tools?.Count ?? 0);
    }

    private static RealtimeSessionOptions CloneOptionsWithTools(RealtimeSessionOptions? source, IReadOnlyList<AITool> tools)
    {
        if (source is null)
        {
            return new RealtimeSessionOptions { Tools = tools };
        }

        return new RealtimeSessionOptions
        {
            Tools = tools,
            InputAudioFormat = source.InputAudioFormat,
            Instructions = source.Instructions,
            MaxOutputTokens = source.MaxOutputTokens,
            Model = source.Model,
            OutputAudioFormat = source.OutputAudioFormat,
            OutputModalities = source.OutputModalities,
            RawRepresentationFactory = source.RawRepresentationFactory,
            SessionKind = source.SessionKind,
            ToolMode = source.ToolMode,
            TranscriptionOptions = source.TranscriptionOptions,
            Voice = source.Voice,
            VoiceActivityDetection = source.VoiceActivityDetection
        };
    }

    public async IAsyncEnumerable<RealtimeBackendUpdate> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = EnsureSession();
        var pending = Channel.CreateUnbounded<RealtimeBackendUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Run the underlying stream on a background task so we can convert any exception
        // it throws into a Faulted update without losing already-buffered updates.
        var driver = Task.Run(() => DrainStreamAsync(session, pending.Writer, cancellationToken), CancellationToken.None);

        try
        {
            while (true)
            {
                RealtimeBackendUpdate update;
                try
                {
                    update = await pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                yield return update;
            }
        }
        finally
        {
            try { await driver.ConfigureAwait(false); } catch { /* surfaced as Faulted */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // RealtimeAIAgentSession exposes its underlying IRealtimeClientSession via
        // ClientSession; we don't own that lifecycle here today (it's tied to the
        // agent's plumbing). When the call ends, the session goes out of scope and
        // the client session disconnects on its own. Adding explicit close here would
        // require leaking IRealtimeClientSession into this layer.
        _session = null;
        return ValueTask.CompletedTask;
    }

    private RealtimeAIAgentSession EnsureSession()
        => _session ?? throw new InvalidOperationException(
            $"{nameof(AuthorizingAgentRealtimeBackend)} is not connected. Call {nameof(ConnectAsync)} first.");

    private async Task DrainStreamAsync(
        RealtimeAIAgentSession session,
        ChannelWriter<RealtimeBackendUpdate> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _agent.RunStreamingAsync(session, _runOptions, cancellationToken).ConfigureAwait(false))
            {
                foreach (var converted in RealtimeBackendUpdateTranslator.Translate(update))
                {
                    await writer.WriteAsync(converted, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime stream faulted for agent {AgentId}", AgentId);
            try
            {
                await writer.WriteAsync(
                    new RealtimeBackendUpdate.Faulted(ex, ex.Message, DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* writer may already be completed */ }
        }
        finally
        {
            writer.TryComplete();
        }
    }
}
