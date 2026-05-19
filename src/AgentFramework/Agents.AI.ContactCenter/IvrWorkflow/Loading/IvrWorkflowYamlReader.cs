using System.Collections.Generic;
using System.Text;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// Parses raw YAML into a <see cref="IvrWorkflowDocument"/>. Wraps a configured
/// <see cref="IDeserializer"/> so the parsing behavior (naming convention, unknown
/// property handling) is consistent everywhere YAML is read.
/// </summary>
public static class IvrWorkflowYamlReader
{
    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Deserialize YAML text into a <see cref="IvrWorkflowDocument"/>.
    /// Throws <see cref="IvrWorkflowYamlException"/> when the document is empty or unparsable.
    /// </summary>
    public static IvrWorkflowDocument Parse(string yaml, string? sourceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        try
        {
            var doc = s_deserializer.Deserialize<IvrWorkflowDocument>(yaml)
                ?? throw new IvrWorkflowYamlException(
                    $"YAML document from '{sourceName ?? "<unknown>"}' deserialized to null.");
            return doc;
        }
        catch (YamlException ex)
        {
            var sb = new StringBuilder();
            sb.Append("Failed to parse IVR workflow YAML");
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                sb.Append(" from '").Append(sourceName).Append('\'');
            }
            sb.Append(" at ").Append(ex.Start).Append(": ").Append(ex.Message);
            throw new IvrWorkflowYamlException(sb.ToString(), ex);
        }
    }
}

/// <summary>Thrown when a workflow YAML document cannot be parsed.</summary>
public sealed class IvrWorkflowYamlException : Exception
{
    public IvrWorkflowYamlException(string message) : base(message) { }
    public IvrWorkflowYamlException(string message, Exception inner) : base(message, inner) { }
}
