using System;
using System.Collections.Generic;
using System.Text;

namespace Extensions.AI.Realtime;

public class VoiceActivityDetectionOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="VoiceActivityDetectionOptions"/> class.
    /// </summary>
    public VoiceActivityDetectionOptions()
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether server-side voice activity detection is enabled.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the server automatically detects speech start and end,
    /// and may automatically trigger responses when the user stops speaking.
    /// When <see langword="false"/>, turn detection is fully disabled and the client controls
    /// turn boundaries manually (e.g., via audio buffer commit and explicit response creation).
    /// Other properties on this class, such as <see cref="AllowInterruption"/>, only take effect
    /// when this property is <see langword="true"/>.
    /// The default is <see langword="true"/>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the user's speech can interrupt the model's audio output.
    /// </summary>
    /// <remarks>
    /// This property is only meaningful when <see cref="Enabled"/> is <see langword="true"/>.
    /// When voice activity detection is disabled, the server does not detect speech, so interruption
    /// does not apply.
    /// When <see langword="true"/>, the model's response will be cut off when the user starts speaking (barge-in).
    /// When <see langword="false"/>, the model's response will continue to completion regardless of user input.
    /// The default is <see langword="true"/>.
    /// Not all providers support this option; those that do not will ignore it.
    /// </remarks>
    public bool AllowInterruption { get; set; } = true;
}
