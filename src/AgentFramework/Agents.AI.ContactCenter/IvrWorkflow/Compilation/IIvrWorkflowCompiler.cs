using System.Collections.Generic;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

/// <summary>
/// Lowers a validated <see cref="IvrWorkflowDocument"/> into a
/// <see cref="CompiledIvrWorkflow"/> that includes the runtime
/// <see cref="RealtimeIvrWorkflowDefinition"/> model consumed by existing strategy
/// factories. Implementations resolve tools, guards, intents, and strategy policy.
/// </summary>
public interface IIvrWorkflowCompiler
{
    CompiledIvrWorkflow Compile(IvrWorkflowDocument document, IvrWorkflowSourceEntry? source = null);
}

/// <summary>Thrown when an <see cref="IvrWorkflowDocument"/> cannot be compiled.</summary>
public sealed class IvrWorkflowCompilationException : System.Exception
{
    public IvrWorkflowCompilationException(string workflow, IReadOnlyList<string> errors)
        : base($"IVR workflow '{workflow}' failed to compile:" + System.Environment.NewLine + " - " + string.Join(System.Environment.NewLine + " - ", errors))
    {
        Workflow = workflow;
        Errors = errors;
    }

    public string Workflow { get; }
    public IReadOnlyList<string> Errors { get; }
}
