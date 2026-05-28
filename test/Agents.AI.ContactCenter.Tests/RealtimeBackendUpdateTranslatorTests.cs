using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies.RealtimeVoice;
using Extensions.AI.Contents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.RealtimeVoice.Azure.Tests.Proposed;

/// <summary>
/// Pins down the wire-level behavior of the production
/// <see cref="AuthorizingAgentRealtimeBackend"/> adapter by testing its translation
/// step in isolation. Standing up a real <c>IRealtimeClient</c> for an end-to-end
/// adapter test would require a substantial fake; the translation function carries
/// the only logic specific to the new shape.
/// </summary>
public class RealtimeBackendUpdateTranslatorTests
{
    [Fact]
    public void DataContent_with_audio_translates_to_Audio_update()
    {
        var pcm = new byte[] { 0x01, 0x02, 0x03 };
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            CreatedAt = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero),
            Contents = [new DataContent(pcm, "audio/pcm")]
        };

        var result = RealtimeBackendUpdateTranslator.Translate(update).ToList();

        var audio = Assert.IsType<RealtimeBackendUpdate.Audio>(Assert.Single(result));
        Assert.Equal(3, audio.Pcm.Length);
        Assert.Equal(update.CreatedAt, audio.At);
    }

    [Fact]
    public void Empty_DataContent_is_dropped()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new DataContent(ReadOnlyMemory<byte>.Empty, "audio/pcm")]
        };

        Assert.Empty(RealtimeBackendUpdateTranslator.Translate(update));
    }

    [Fact]
    public void AudioTranscriptionContent_emits_non_final_Transcript()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.User,
            Contents = [new AudioTranscriptionContent { Text = "I need help with billing" }]
        };

        var result = RealtimeBackendUpdateTranslator.Translate(update).ToList();

        var transcript = Assert.IsType<RealtimeBackendUpdate.Transcript>(Assert.Single(result));
        Assert.Equal("user", transcript.Speaker);
        Assert.Equal("I need help with billing", transcript.Text);
        Assert.False(transcript.IsFinal);
    }

    [Fact]
    public void TextContent_from_assistant_emits_AgentText()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("How can I help you today?")]
        };

        var result = RealtimeBackendUpdateTranslator.Translate(update).ToList();

        var text = Assert.IsType<RealtimeBackendUpdate.AgentText>(Assert.Single(result));
        Assert.Equal("How can I help you today?", text.Text);
    }

    [Fact]
    public void TextContent_from_user_emits_final_Transcript()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.User,
            Contents = [new TextContent("Yes please")]
        };

        var result = RealtimeBackendUpdateTranslator.Translate(update).ToList();

        var transcript = Assert.IsType<RealtimeBackendUpdate.Transcript>(Assert.Single(result));
        Assert.Equal("user", transcript.Speaker);
        Assert.Equal("Yes please", transcript.Text);
        Assert.True(transcript.IsFinal);
    }

    [Fact]
    public void Whitespace_text_is_dropped()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("   ")]
        };

        Assert.Empty(RealtimeBackendUpdateTranslator.Translate(update));
    }

    [Fact]
    public void RealtimeVadContent_is_dropped()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new RealtimeVadContent(VadEventType.InputSpeechStarted)]
        };

        Assert.Empty(RealtimeBackendUpdateTranslator.Translate(update));
    }

    [Fact]
    public void Mixed_contents_translate_in_order()
    {
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new DataContent(new byte[] { 0x10 }, "audio/pcm"),
                new TextContent("Hello!"),
                new AudioTranscriptionContent { Text = "Hello!" }
            ]
        };

        var result = RealtimeBackendUpdateTranslator.Translate(update).ToList();

        Assert.Collection(result,
            r => Assert.IsType<RealtimeBackendUpdate.Audio>(r),
            r => Assert.IsType<RealtimeBackendUpdate.AgentText>(r),
            r =>
            {
                var t = Assert.IsType<RealtimeBackendUpdate.Transcript>(r);
                Assert.Equal("assistant", t.Speaker);
                Assert.False(t.IsFinal);
            });
    }

    [Fact]
    public void Missing_CreatedAt_falls_back_to_UtcNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var update = new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            CreatedAt = null,
            Contents = [new TextContent("hi")]
        };

        var text = Assert.IsType<RealtimeBackendUpdate.AgentText>(
            Assert.Single(RealtimeBackendUpdateTranslator.Translate(update)));

        Assert.InRange(text.At, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
