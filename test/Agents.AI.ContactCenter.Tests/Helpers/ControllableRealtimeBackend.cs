using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Tests.Helpers;

/// <summary>
/// In-memory <see cref="IRealtimeVoiceBackend"/> test double the realtime tier strategy
/// can drive without standing up a real model. Records every audio chunk, prompt push,
/// and tool-list update, and lets the test emit synthetic
/// <see cref="RealtimeBackendUpdate"/>s on demand.
/// </summary>
internal sealed class ControllableRealtimeBackend : IRealtimeVoiceBackend
{
    private readonly Channel<RealtimeBackendUpdate> _updates = Channel.CreateUnbounded<RealtimeBackendUpdate>();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ControllableRealtimeBackend(string agentId, string agentDisplayName)
    {
        AgentId = agentId;
        AgentDisplayName = agentDisplayName;
    }

    public string AgentId { get; }
    public string AgentDisplayName { get; }
    public string? LastSystemPrompt { get; private set; }
    public List<ReadOnlyMemory<byte>> ReceivedAudio { get; } = [];
    public List<IReadOnlyList<AITool>> ToolUpdates { get; } = [];
    public List<string> ReceivedUserText { get; } = [];

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        ReceivedAudio.Add(pcm);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateSystemPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        LastSystemPrompt = prompt;
        return ValueTask.CompletedTask;
    }

    public ValueTask SendUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ReceivedUserText.Add(text);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateToolsAsync(IEnumerable<AITool> tools, CancellationToken cancellationToken = default)
    {
        ToolUpdates.Add(tools.ToList().AsReadOnly());
        return ValueTask.CompletedTask;
    }

    public ValueTask StartResponseAsync(
        IEnumerable<AITool>? tools = null,
        string? instruction = null,
        CancellationToken cancellationToken = default)
    {
        if (tools is not null)
        {
            ToolUpdates.Add(tools.ToList().AsReadOnly());
        }
        if (instruction is not null)
        {
            LastSystemPrompt = instruction;
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<RealtimeBackendUpdate> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public ValueTask EmitAsync(RealtimeBackendUpdate update)
        => _updates.Writer.WriteAsync(update);

    public ValueTask FaultAsync(Exception ex)
        => _updates.Writer.WriteAsync(new RealtimeBackendUpdate.Faulted(ex, ex.Message, DateTimeOffset.UtcNow));

    public async Task<bool> WaitForConnectAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_connected.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == _connected.Task;
    }

    public ValueTask DisposeAsync()
    {
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
