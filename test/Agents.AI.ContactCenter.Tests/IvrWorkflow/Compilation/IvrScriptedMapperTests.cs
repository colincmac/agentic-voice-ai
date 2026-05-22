using System;
using System.Collections.Generic;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Compilation;

public class IvrScriptedMapperTests
{
    [Fact]
    public void Lowers_shared_prompts_knobs_and_nlu_overrides()
    {
        var errors = new List<string>();
        var doc = new IvrScriptedStageDocument
        {
            SsmlPrompt = "<speak>hi</speak>",
            AudioFile = "https://cdn.example/entry.wav",
            OnErrorPrompt = "again?",
            OnErrorAudioFile = "https://cdn.example/err.wav",
            OnNoInputPrompt = "hello?",
            OnConfirmPrompt = "got it",
            OnHandoffPrompt = "transferring",
            MaxNoMatch = 4,
            MaxNoInput = 1,
            ConfidenceThreshold = 0.65,
            NoInputTimeoutMs = 3500,
            Examples = { "check my balance" },
            Nlu = new IvrNluDocument
            {
                SsmlPrompt = "<speak>say balance</speak>",
                Intents =
                {
                    new IvrIntentDocument { Name = "balance", NextStage = "verify" },
                    new IvrIntentDocument { Name = "noop" }, // skipped — no nextStage
                },
            },
        };

        var cfg = IvrScriptedMapper.Map(doc, stageId: "welcome", errors);

        Assert.NotNull(cfg);
        Assert.Empty(errors);
        Assert.Equal("<speak>hi</speak>", cfg!.SsmlPrompt);
        Assert.Equal(new Uri("https://cdn.example/entry.wav"), cfg.AudioFile);
        Assert.Equal("again?", cfg.OnErrorPrompt);
        Assert.Equal(new Uri("https://cdn.example/err.wav"), cfg.OnErrorAudioFile);
        Assert.Equal("hello?", cfg.OnNoInputPrompt);
        Assert.Equal("got it", cfg.OnConfirmPrompt);
        Assert.Equal("transferring", cfg.OnHandoffPrompt);
        Assert.Equal(4, cfg.MaxNoMatch);
        Assert.Equal(1, cfg.MaxNoInput);
        Assert.Equal(0.65, cfg.ConfidenceThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), cfg.NoInputTimeout);
        Assert.Single(cfg.Examples);

        Assert.NotNull(cfg.Nlu);
        Assert.Equal("<speak>say balance</speak>", cfg.Nlu!.SsmlPromptOverride);
        Assert.Single(cfg.Nlu.IntentTransitions);
        Assert.Equal("verify", cfg.Nlu.IntentTransitions["balance"]);
    }

    [Fact]
    public void Reports_out_of_range_threshold_and_bad_uri()
    {
        var errors = new List<string>();
        var doc = new IvrScriptedStageDocument
        {
            ConfidenceThreshold = 2.0,
            AudioFile = "not-a-uri",
            MaxNoMatch = -1,
            NoInputTimeoutMs = -5,
        };

        var cfg = IvrScriptedMapper.Map(doc, stageId: "s1", errors);

        Assert.NotNull(cfg);
        Assert.Contains(errors, e => e.Contains("confidenceThreshold"));
        Assert.Contains(errors, e => e.Contains("audioFile") && e.Contains("absolute URI"));
        Assert.Contains(errors, e => e.Contains("maxNoMatch"));
        Assert.Contains(errors, e => e.Contains("noInputTimeoutMs"));

        // Clamping still produces a usable runtime config.
        Assert.InRange(cfg!.ConfidenceThreshold, 0.0, 1.0);
        Assert.Equal(0, cfg.MaxNoMatch);
        Assert.Equal(TimeSpan.Zero, cfg.NoInputTimeout);
    }

    [Fact]
    public void Empty_document_returns_null()
    {
        var errors = new List<string>();
        var cfg = IvrScriptedMapper.Map(new IvrScriptedStageDocument(), stageId: "s", errors);

        Assert.Empty(errors);
        Assert.Null(cfg);
    }

    [Fact]
    public void Dtmf_only_emits_dtmf_sub_config_and_null_nlu()
    {
        var errors = new List<string>();
        var doc = new IvrScriptedStageDocument
        {
            Dtmf = new IvrDtmfDocument
            {
                SsmlPrompt = "press 1",
                Options =
                {
                    new IvrDtmfOptionDocument { Digit = "1", Label = "balance", NextStage = "verify" },
                },
            },
        };

        var cfg = IvrScriptedMapper.Map(doc, stageId: "welcome", errors);

        Assert.NotNull(cfg);
        Assert.Empty(errors);
        Assert.Null(cfg!.Nlu);
        Assert.NotNull(cfg.Dtmf);
        Assert.Equal("press 1", cfg.Dtmf!.SsmlPromptOverride);
        Assert.NotNull(cfg.Dtmf.MenuOptions);
        Assert.Contains('1', cfg.Dtmf.MenuOptions!.Keys);
    }
}
