using Agents.AI.ContactCenter.IvrWorkflow.Definition;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Definition;

public class IvrScriptedDocumentYamlTests
{
    [Fact]
    public void Parses_full_scripted_block_with_nlu_overrides()
    {
        const string yaml = """
            name: t
            stages:
              - id: welcome
                goal: Greet the caller
                scripted:
                  onErrorPrompt: "Please say balance, billing, or agent."
                  onNoInputPrompt: "Sorry, I didn't hear anything."
                  onConfirmPrompt: "Got it."
                  onHandoffPrompt: "One moment."
                  maxNoMatch: 3
                  maxNoInput: 1
                  confidenceThreshold: 0.7
                  noInputTimeoutMs: 4000
                  examples:
                    - check my balance
                  nlu:
                    ssmlPrompt: |
                      <speak xml:lang="en-US">Welcome.</speak>
                    intents:
                      - name: balance
                        examples: [ "balance please" ]
                        nextStage: closing
              - id: closing
                terminal: true
            """;

        var doc = IvrWorkflowYamlReader.Parse(yaml);
        var stage = doc.Stages[0];

        Assert.NotNull(stage.Scripted);
        var scripted = stage.Scripted!;
        Assert.Equal("Please say balance, billing, or agent.", scripted.OnErrorPrompt);
        Assert.Equal("Sorry, I didn't hear anything.", scripted.OnNoInputPrompt);
        Assert.Equal("Got it.", scripted.OnConfirmPrompt);
        Assert.Equal("One moment.", scripted.OnHandoffPrompt);
        Assert.Equal(3, scripted.MaxNoMatch);
        Assert.Equal(1, scripted.MaxNoInput);
        Assert.Equal(0.7, scripted.ConfidenceThreshold);
        Assert.Equal(4000, scripted.NoInputTimeoutMs);
        Assert.Single(scripted.Examples);

        Assert.NotNull(scripted.Nlu);
        Assert.StartsWith("<speak", scripted.Nlu!.SsmlPrompt!.TrimStart());
        Assert.Single(scripted.Nlu.Intents);
        Assert.Equal("balance", scripted.Nlu.Intents[0].Name);
        Assert.Equal("closing", scripted.Nlu.Intents[0].NextStage);
    }

    [Fact]
    public void Validator_flags_invalid_threshold_and_dual_prompt()
    {
        var doc = new IvrWorkflowDocument
        {
            Name = "t",
            Stages =
            {
                new IvrStageDocument
                {
                    Id = "welcome",
                    Scripted = new IvrScriptedStageDocument
                    {
                        ConfidenceThreshold = 1.5,
                        OnErrorPrompt = "say again",
                        OnErrorAudioFile = "https://cdn/x.wav",
                    },
                    Transitions = { new IvrTransitionDocument { To = "closing" } },
                },
                new IvrStageDocument { Id = "closing", Terminal = true },
            },
        };

        var result = IvrWorkflowDocumentValidator.Validate(doc);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("confidenceThreshold"));
        Assert.Contains(result.Errors, e => e.Contains("scripted.error"));
    }

    [Fact]
    public void Stage_without_scripted_block_round_trips_cleanly()
    {
        const string yaml = """
            name: t
            stages:
              - id: only
                terminal: true
                realtime:
                  instructions: [ Say hi. ]
            """;
        var doc = IvrWorkflowYamlReader.Parse(yaml);

        Assert.Null(doc.Stages[0].Scripted);
        Assert.NotNull(doc.Stages[0].Realtime);
    }
}

