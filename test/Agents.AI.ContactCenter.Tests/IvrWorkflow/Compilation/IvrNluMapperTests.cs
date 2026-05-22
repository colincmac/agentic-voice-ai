using System;
using System.Collections.Generic;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Compilation;

public class IvrNluMapperTests
{
    [Fact]
    public void Lowers_prompt_fields_audio_uris_and_policy_knobs()
    {
        var errors = new List<string>();
        var doc = new IvrNluDocument
        {
            SsmlPrompt = "<speak>hi</speak>",
            AudioFile = "https://cdn.example/entry.wav",
            OnNoMatchPrompt = "again?",
            OnNoMatchAudioFile = "https://cdn.example/nomatch.wav",
            OnNoInputPrompt = "hello?",
            OnConfirmPrompt = "got it",
            OnHandoffPrompt = "transferring",
            MaxNoMatch = 4,
            MaxNoInput = 1,
            ConfidenceThreshold = 0.65,
            NoInputTimeoutMs = 3500,
            Examples = { "check my balance" },
            Intents =
            {
                new IvrIntentDocument { Name = "balance", NextStage = "verify" },
                new IvrIntentDocument { Name = "noop" }, // skipped — no nextStage
            },
        };

        var cfg = IvrNluMapper.Map(doc, stageId: "welcome", errors);

        Assert.Empty(errors);
        Assert.Equal("<speak>hi</speak>", cfg.SsmlPromptOverride);
        Assert.Equal(new Uri("https://cdn.example/entry.wav"), cfg.AudioFile);
        Assert.Equal("again?", cfg.OnNoMatchPrompt);
        Assert.Equal(new Uri("https://cdn.example/nomatch.wav"), cfg.OnNoMatchAudioFile);
        Assert.Equal("hello?", cfg.OnNoInputPrompt);
        Assert.Equal("got it", cfg.OnConfirmPrompt);
        Assert.Equal("transferring", cfg.OnHandoffPrompt);
        Assert.Equal(4, cfg.MaxNoMatch);
        Assert.Equal(1, cfg.MaxNoInput);
        Assert.Equal(0.65, cfg.ConfidenceThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), cfg.NoInputTimeout);
        Assert.Single(cfg.Examples);
        Assert.Single(cfg.IntentTransitions);
        Assert.Equal("verify", cfg.IntentTransitions["balance"]);
    }

    [Fact]
    public void Reports_out_of_range_threshold_and_bad_uri()
    {
        var errors = new List<string>();
        var doc = new IvrNluDocument
        {
            ConfidenceThreshold = 2.0,
            AudioFile = "not-a-uri",
            MaxNoMatch = -1,
            NoInputTimeoutMs = -5,
        };

        var cfg = IvrNluMapper.Map(doc, stageId: "s1", errors);

        Assert.Contains(errors, e => e.Contains("confidenceThreshold"));
        Assert.Contains(errors, e => e.Contains("audioFile") && e.Contains("absolute URI"));
        Assert.Contains(errors, e => e.Contains("maxNoMatch"));
        Assert.Contains(errors, e => e.Contains("noInputTimeoutMs"));

        // Clamping still produces a usable runtime config.
        Assert.InRange(cfg.ConfidenceThreshold, 0.0, 1.0);
        Assert.Equal(0, cfg.MaxNoMatch);
        Assert.Equal(TimeSpan.Zero, cfg.NoInputTimeout);
    }

    [Fact]
    public void Empty_document_produces_defaults()
    {
        var errors = new List<string>();
        var cfg = IvrNluMapper.Map(new IvrNluDocument(), stageId: "s", errors);

        Assert.Empty(errors);
        Assert.Null(cfg.SsmlPromptOverride);
        Assert.Null(cfg.AudioFile);
        Assert.Empty(cfg.IntentTransitions);
        Assert.Equal(2, cfg.MaxNoMatch);
        Assert.Equal(2, cfg.MaxNoInput);
        Assert.Equal(0.5, cfg.ConfidenceThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), cfg.NoInputTimeout);
    }
}
