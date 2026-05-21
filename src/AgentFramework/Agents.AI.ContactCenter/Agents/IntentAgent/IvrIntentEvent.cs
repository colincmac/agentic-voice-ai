using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.Media.Analysis;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;

namespace Agents.AI.ContactCenter.Agents.IntentAgent;

/// <summary>
/// One classification event emitted by <see cref="IvrIntentAgent.ClassifyAudioStreamAsync"/>.
/// Pairs the final transcript segment with the intent classifier's verdict so callers
/// can drive routing decisions or surface insights alongside a primary realtime agent.
/// </summary>
/// <param name="Transcript">The final transcript segment that produced this classification.</param>
/// <param name="Intent">The intent classifier verdict for <paramref name="Transcript"/>.</param>
/// <param name="At">UTC timestamp the classification completed.</param>
public sealed record IvrIntentEvent(
    TranscriptSegment Transcript,
    IntentResult Intent,
    DateTimeOffset At);
