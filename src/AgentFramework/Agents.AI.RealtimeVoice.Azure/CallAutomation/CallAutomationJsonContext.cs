using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents.AI.RealtimeVoice.Azure.CallAutomation;

namespace Agents.AI.RealtimeVoice.Azure.Calling.CallAutomation;


[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(TeamsExtensionAssignmentCreateOrUpdateRequest))]
[JsonSerializable(typeof(TeamsExtensionPrincipalType))]
[JsonSerializable(typeof(TeamsExtensionAssignmentResponse))]
public partial class CallAutomationJsonContext : JsonSerializerContext
{
}
