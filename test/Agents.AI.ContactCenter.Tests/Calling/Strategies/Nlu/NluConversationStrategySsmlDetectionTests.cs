using Agents.AI.ContactCenter.Calling.Strategies.Nlu;

namespace Agents.AI.ContactCenter.Tests.Calling.Strategies.Nlu;

/// <summary>
/// Focused tests for the SSML detection branch that drives whether
/// <see cref="NluConversationStrategy"/> dispatches a prompt override as
/// <c>SynthesizerInputFormat.SSML</c> or <c>SynthesizerInputFormat.Text</c>.
/// The full strategy is exercised end-to-end via the higher-level call-session
/// tests; this class isolates the decision so a regression in SSML routing is
/// caught without standing up the live intent agent.
/// </summary>
public class NluConversationStrategySsmlDetectionTests
{
    [Theory]
    [InlineData("<speak version=\"1.0\">hi</speak>", true)]
    [InlineData("   <speak>hi</speak>", true)]
    [InlineData("<SPEAK>hi</SPEAK>", true)]
    [InlineData("Just plain text.", false)]
    [InlineData("<voice>hi</voice>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeSsml_recognizes_speak_root_element(string? input, bool expected)
    {
        Assert.Equal(expected, NluConversationStrategy.LooksLikeSsml(input));
    }
}
