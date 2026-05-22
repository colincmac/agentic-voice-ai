using Agents.AI.ContactCenter.IvrWorkflow.Definition;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Definition;

public class IvrNluDocumentYamlTests
{
    [Fact]
    public void Parses_full_nlu_block_with_ssml_overrides()
    {
        const string yaml = """
            name: t
            stages:
              - id: welcome
                goal: Greet the caller
                nlu:
                  ssmlPrompt: |
                    <speak xml:lang="en-US">Welcome.</speak>
                  onNoMatchPrompt: "Please say balance, billing, or agent."
                  onNoInputPrompt: "Sorry, I didn't hear anything."
                  onConfirmPrompt: "Got it."
                  onHandoffPrompt: "One moment."
                  maxNoMatch: 3
                  maxNoInput: 1
                  confidenceThreshold: 0.7
                  noInputTimeoutMs: 4000
                  examples:
                    - check my balance
                  intents:
                    - name: balance
                      examples: [ "balance please" ]
                      nextStage: closing
              - id: closing
                terminal: true
            """;

        var doc = IvrWorkflowYamlReader.Parse(yaml);
        var stage = doc.Stages[0];

        Assert.NotNull(stage.Nlu);
        Assert.StartsWith("<speak", stage.Nlu!.SsmlPrompt!.TrimStart());
        Assert.Equal("Please say balance, billing, or agent.", stage.Nlu.OnNoMatchPrompt);
        Assert.Equal("Sorry, I didn't hear anything.", stage.Nlu.OnNoInputPrompt);
        Assert.Equal("Got it.", stage.Nlu.OnConfirmPrompt);
        Assert.Equal("One moment.", stage.Nlu.OnHandoffPrompt);
        Assert.Equal(3, stage.Nlu.MaxNoMatch);
        Assert.Equal(1, stage.Nlu.MaxNoInput);
        Assert.Equal(0.7, stage.Nlu.ConfidenceThreshold);
        Assert.Equal(4000, stage.Nlu.NoInputTimeoutMs);
        Assert.Single(stage.Nlu.Examples);
        Assert.Single(stage.Nlu.Intents);
        Assert.Equal("balance", stage.Nlu.Intents[0].Name);
        Assert.Equal("closing", stage.Nlu.Intents[0].NextStage);
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
                    Nlu = new IvrNluDocument
                    {
                        ConfidenceThreshold = 1.5,
                        OnNoMatchPrompt = "say again",
                        OnNoMatchAudioFile = "https://cdn/x.wav",
                    },
                    Transitions = { new IvrTransitionDocument { To = "closing" } },
                },
                new IvrStageDocument { Id = "closing", Terminal = true },
            },
        };

        var result = IvrWorkflowDocumentValidator.Validate(doc);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("confidenceThreshold"));
        Assert.Contains(result.Errors, e => e.Contains("nlu.noMatch"));
    }

    [Fact]
    public void Stage_without_nlu_block_round_trips_cleanly()
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

        Assert.Null(doc.Stages[0].Nlu);
        Assert.NotNull(doc.Stages[0].Realtime);
    }
}
