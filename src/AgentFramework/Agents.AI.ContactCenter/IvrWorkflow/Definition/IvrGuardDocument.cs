using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Agents.AI.ContactCenter.IvrWorkflow.Definition;

/// <summary>
/// Declarative guard / precondition. The <see cref="Type"/> selects the built-in
/// guard kind; remaining fields are guard-specific arguments. Custom guards can be
/// registered through <see cref="Guards.IIvrGuardFactory"/>.
/// </summary>
public sealed class IvrGuardDocument
{
    /// <summary>
    /// Built-in kinds: <c>auth</c>, <c>state</c>, <c>previousStage</c>, <c>predicate</c>.
    /// Anything else is dispatched to a registered <see cref="Guards.IIvrGuardFactory"/>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>For <c>auth</c>: the required minimum <see cref="CallerVerificationLevel"/>.</summary>
    [YamlMember(Alias = "level")]
    public string? Level { get; set; }

    /// <summary>For <c>state</c>: the required state key (may be repeated via <see cref="Keys"/>).</summary>
    [YamlMember(Alias = "key")]
    public string? Key { get; set; }

    /// <summary>For <c>state</c>: multiple required state keys.</summary>
    [YamlMember(Alias = "keys")]
    public List<string> Keys { get; set; } = [];

    /// <summary>For <c>previousStage</c>: the prerequisite stage id.</summary>
    [YamlMember(Alias = "stage")]
    public string? Stage { get; set; }

    /// <summary>For <c>predicate</c>: a registered predicate name to look up.</summary>
    [YamlMember(Alias = "predicate")]
    public string? Predicate { get; set; }

    /// <summary>Optional override for the guard's failure message.</summary>
    [YamlMember(Alias = "message")]
    public string? Message { get; set; }

    /// <summary>Free-form guard arguments for custom guard kinds.</summary>
    [YamlMember(Alias = "args")]
    public Dictionary<string, object?> Args { get; set; } = [];
}
