using System;
using System.Collections.Generic;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Definition;

namespace Agents.AI.ContactCenter.IvrWorkflow.Guards;

/// <summary>
/// Built-in guard factories that map YAML <c>type</c> values to existing
/// <see cref="IIvrStepGuard"/> implementations. Registered automatically when the
/// declarative loader is added to DI.
/// </summary>
public static class BuiltInGuardFactories
{
    /// <summary>Construct the set of built-in factories, in registration order.</summary>
    public static IReadOnlyList<IIvrGuardFactory> CreateAll() =>
    [
        new AuthGuardFactory(),
        new StateGuardFactory(),
        new PreviousStageGuardFactory(),
        new PredicateGuardFactory(),
    ];
}

/// <summary>Factory for <c>type: auth</c> guards backed by <see cref="RequiredAuthLevelGuard"/>.</summary>
internal sealed class AuthGuardFactory : IIvrGuardFactory
{
    public string Type => "auth";

    public IIvrStepGuard Create(IvrGuardDocument document, IIvrGuardBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!TryParseAuthLevel(document.Level, out var level))
        {
            throw new InvalidOperationException(
                $"Workflow '{context.WorkflowName}' stage '{context.StageId ?? "(global)"}': auth guard 'level' value '{document.Level}' is not recognized.");
        }
        return new RequiredAuthLevelGuard(level, document.Message);
    }

    private static bool TryParseAuthLevel(string? value, out CallerVerificationLevel level)
    {
        level = CallerVerificationLevel.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => Set(out level, CallerVerificationLevel.None),
            "animatch" or "ani" or "phone" => Set(out level, CallerVerificationLevel.AniMatch),
            "knowledgebased" or "knowledge" or "kba" or "pin" => Set(out level, CallerVerificationLevel.KnowledgeBased),
            "multifactor" or "mfa" => Set(out level, CallerVerificationLevel.MultiFactor),
            "voicebiometric" or "biometric" or "voice" => Set(out level, CallerVerificationLevel.VoiceBiometric),
            "entraverifiedid" or "verifiedid" => Set(out level, CallerVerificationLevel.EntraVerifiedId),
            "strong" => Set(out level, CallerVerificationLevel.Strong),
            _ => false,
        };

        static bool Set(out CallerVerificationLevel l, CallerVerificationLevel v)
        {
            l = v;
            return true;
        }
    }
}

/// <summary>Factory for <c>type: state</c> guards. Accepts a single <c>key</c> or a list of <c>keys</c>.</summary>
internal sealed class StateGuardFactory : IIvrGuardFactory
{
    public string Type => "state";

    public IIvrStepGuard Create(IvrGuardDocument document, IIvrGuardBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Keys.Count > 0)
        {
            return new RequiredStateKeysGuard(document.Keys, document.Message);
        }
        if (!string.IsNullOrWhiteSpace(document.Key))
        {
            return new RequiredStateGuard(document.Key, document.Message);
        }

        throw new InvalidOperationException(
            $"Workflow '{context.WorkflowName}' stage '{context.StageId ?? "(global)"}': state guard requires either 'key' or 'keys'.");
    }
}

/// <summary>Factory for <c>type: previousStage</c> guards backed by <see cref="PreviousStepCompletedGuard"/>.</summary>
internal sealed class PreviousStageGuardFactory : IIvrGuardFactory
{
    public string Type => "previousStage";

    public IIvrStepGuard Create(IvrGuardDocument document, IIvrGuardBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.Stage))
        {
            throw new InvalidOperationException(
                $"Workflow '{context.WorkflowName}' stage '{context.StageId ?? "(global)"}': previousStage guard requires 'stage'.");
        }
        return new PreviousStepCompletedGuard(document.Stage, document.Message);
    }
}

/// <summary>Factory for <c>type: predicate</c> guards backed by <see cref="IIvrPredicateRegistry"/>.</summary>
internal sealed class PredicateGuardFactory : IIvrGuardFactory
{
    public string Type => "predicate";

    public IIvrStepGuard Create(IvrGuardDocument document, IIvrGuardBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.Predicate))
        {
            throw new InvalidOperationException(
                $"Workflow '{context.WorkflowName}' stage '{context.StageId ?? "(global)"}': predicate guard requires 'predicate'.");
        }
        return context.Predicates.Resolve(document.Predicate, document.Message);
    }
}
