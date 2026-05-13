using Agents.AI.Extensions.LiveVoice.IvrWorkflow;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

namespace Showcase.Agent.VoiceAgent;

public static class IvrSampleWorkflow
{

    public static RealtimeIvrWorkflowDefinition DtmfOnly() => new RealtimeIvrWorkflowDefinition()
    {
        Name = "dtmf-ivr",
        BasePrompt = new RealtimePrompt(),
        Steps =
        [
            new RealtimeIvrWorkflowStep
            {
                Id = "01_language",
                ConversationState = new ConversationState
                {
                    Id = "language",
                    Description = "Welcome to Contoso",
                    Goal = "Route the caller",
                    Instructions = ["Greet the caller and offer menu"],
                    Transitions =
                    [
                        new StateTransition { NextStep = "main_menu_eng", Condition = "selected language" },
                        new StateTransition { NextStep = "main_menu_esp", Condition = "selected language" }
                    ]
                },
                StepDtmfConfiguration = new StepDtmfConfiguration(maxNumberOfDigits: 1)
                {
                    SsmlPromptOverride = """
                    <speak version="1.0"
                           xmlns="http://www.w3.org/2001/10/synthesis"
                           xml:lang="en-US">
                      <voice name="en-US-Ava:DragonHDLatestNeural">
                        <prosody rate="-8%">
                          Thank you for calling Direct Express.
                          <break time="400ms"/>
                          For English, press 1.
                          <break time="200ms"/>
                          Para español, oprima el dos.
                        </prosody>
                      </voice>
                    </speak>
                    """,
                    MenuOptions = new Dictionary<char, DtmfMenuOption>
                    {
                        ['1'] = new() { Digit = '1', Label = "english", NextStepId = "main_menu_eng" },
                        ['2'] = new() { Digit = '2', Label = "spanish", NextStepId = "main_menu_esp" },
                    }
                }
            },
            new RealtimeIvrWorkflowStep
            {
                Id = "main_menu_eng",
                ConversationState = new ConversationState
                {
                    Id = "main_menu_eng",
                    Description = "Main menu",
                    Instructions = ["Greet the caller and offer menu options"]
                },
                StepDtmfConfiguration = new StepDtmfConfiguration(maxNumberOfDigits: 1)
                {
                    SsmlPromptOverride = """
                    <speak version="1.0"
                           xmlns="http://www.w3.org/2001/10/synthesis"
                           xml:lang="en-US">
                      <voice name="en-US-Ava:DragonHDLatestNeural">
                        <prosody rate="-8%">
                          Welcome to Direct Express automated account services.
                          <break time="700ms"/>
                          Please have your card number available before continuing.
                          <break time="700ms"/>
                          For card activation, press 1.
                          <break time="450ms"/>
                          For your current balance and recent account activity, press 2.
                          <break time="450ms"/>
                          To report a lost or stolen card, press 3.
                          <break time="450ms"/>
                          To report unauthorized charges or possible fraud, press 4.
                          <break time="450ms"/>
                          For replacement card services, press 5.
                          <break time="450ms"/>
                          For account information, fees, or direct deposit questions, press 6.
                          <break time="450ms"/>
                          To repeat these options, press 9.
                          <break time="650ms"/>
                          To speak with a customer service representative, press 0.
                        </prosody>
                      </voice>
                    </speak>
                    """,
                    MenuOptions = new Dictionary<char, DtmfMenuOption>
                    {
                        ['0'] = new() { Digit = '0', Label = "speak with a customer service representative" },
                        ['1'] = new() { Digit = '1', Label = "card activation", NextStepId = "card_activation" },
                    }
                }
            }
        ]
    };
}
