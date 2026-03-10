// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Extensions.AI.OpenTelemetry.SemanticConventions;
using Microsoft.Extensions.Logging;


namespace Microsoft.Extensions.AI;

/// <summary>
/// A composition-based helper class for processing function invocations.
/// Used by both <see cref="FunctionInvokingChatClient"/> and <see cref="FunctionInvokingRealtimeClientSession"/>.
/// </summary>
internal sealed class FunctionInvocationProcessor
{
    private readonly ILogger _logger;
    private readonly ActivitySource? _activitySource;
    private readonly Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> _invokeFunction;
    private readonly Func<Activity?, bool> _isSensitiveDataEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="FunctionInvocationProcessor"/> class.
    /// </summary>
    /// <param name="logger">The logger to use for logging.</param>
    /// <param name="activitySource">The activity source for telemetry.</param>
    /// <param name="invokeFunction">The delegate to invoke a function.</param>
    /// <param name="isSensitiveDataEnabled">
    /// A delegate that determines whether sensitive data logging is enabled.
    /// Receives the invoke agent activity (or null if not in agent context).
    /// Returns true if sensitive data should be logged/tagged, false otherwise.
    /// </param>
    public FunctionInvocationProcessor(
        ILogger logger,
        ActivitySource? activitySource,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> invokeFunction,
        Func<Activity?, bool>? isSensitiveDataEnabled = null)
    {
        _logger = logger;
        _activitySource = activitySource;
        _invokeFunction = invokeFunction;
        _isSensitiveDataEnabled = isSensitiveDataEnabled ?? (_ => false);
    }

    /// <summary>
    /// Processes multiple function calls, either concurrently or serially.
    /// </summary>
    /// <param name="functionCallContents">The function calls to process.</param>
    /// <param name="toolMap">Map from tool name to tool.</param>
    /// <param name="allowConcurrentInvocation">Whether to allow concurrent invocation.</param>
    /// <param name="createContext">Delegate to create a <see cref="FunctionInvocationContext"/> for each function call.</param>
    /// <param name="setCurrentContext">Delegate to set the current context (for AsyncLocal flow).</param>
    /// <param name="captureExceptionsWhenSerial">Whether to capture exceptions when running serially (typically based on consecutive error count).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of function invocation results.</returns>
    public async Task<List<FunctionInvocationResult>> ProcessFunctionCallsAsync(
        List<FunctionCallContent> functionCallContents,
        Dictionary<string, AITool>? toolMap,
        bool allowConcurrentInvocation,
        Func<FunctionCallContent, AIFunction, int, FunctionInvocationContext> createContext,
        Action<FunctionInvocationContext?> setCurrentContext,
        bool captureExceptionsWhenSerial,
        CancellationToken cancellationToken)
    {
        var results = new List<FunctionInvocationResult>();

        if (allowConcurrentInvocation && functionCallContents.Count > 1)
        {
            // Invoke functions concurrently - always capture exceptions in parallel mode
            results.AddRange(await Task.WhenAll(
                from callIndex in Enumerable.Range(0, functionCallContents.Count)
                select ProcessSingleFunctionCallAsync(
                    functionCallContents[callIndex], toolMap, callIndex,
                    createContext, setCurrentContext, captureExceptions: true, cancellationToken)).ConfigureAwait(false));
        }
        else
        {
            // Invoke functions serially
            for (int callIndex = 0; callIndex < functionCallContents.Count; callIndex++)
            {
                var result = await ProcessSingleFunctionCallAsync(
                    functionCallContents[callIndex], toolMap, callIndex,
                    createContext, setCurrentContext, captureExceptionsWhenSerial, cancellationToken).ConfigureAwait(false);

                results.Add(result);

                if (result.Terminate)
                {
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Processes a single function call.
    /// </summary>
    private async Task<FunctionInvocationResult> ProcessSingleFunctionCallAsync(
        FunctionCallContent callContent,
        Dictionary<string, AITool>? toolMap,
        int callIndex,
        Func<FunctionCallContent, AIFunction, int, FunctionInvocationContext> createContext,
        Action<FunctionInvocationContext?> setCurrentContext,
        bool captureExceptions,
        CancellationToken cancellationToken)
    {
        // Look up the AIFunction for the function call. If the requested function isn't available, send back an error.
        if (toolMap is null ||
            !toolMap.TryGetValue(callContent.Name, out AITool? tool))
        {
            FunctionInvocationLogger.LogFunctionNotFound(_logger, callContent.Name);
            return new(terminate: false, FunctionInvocationStatus.NotFound, callContent, result: null, exception: null);
        }

        if (tool is not AIFunction aiFunction)
        {
            FunctionInvocationLogger.LogNonInvocableFunction(_logger, callContent.Name);
            return new(terminate: false, FunctionInvocationStatus.NotFound, callContent, result: null, exception: null);
        }

        var context = createContext(callContent, aiFunction, callIndex);

        try
        {
            setCurrentContext(context);
            var result = await InstrumentedInvokeFunctionAsync(context, cancellationToken).ConfigureAwait(false);
            if (context.Terminate)
            {
                FunctionInvocationLogger.LogFunctionRequestedTermination(_logger, callContent.Name);
            }

            return new(context.Terminate, FunctionInvocationStatus.RanToCompletion, callContent, result, exception: null);
        }
        catch (Exception ex) when (captureExceptions && !cancellationToken.IsCancellationRequested)
        {
            return new(terminate: false, FunctionInvocationStatus.Exception, callContent, result: null, exception: ex);
        }
        finally
        {
            setCurrentContext(null);
        }
    }

    /// <summary>
    /// Invokes the function with instrumentation (logging and telemetry).
    /// </summary>
    private async Task<object?> InstrumentedInvokeFunctionAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        Activity? invokeAgentActivity = FunctionInvocationHelpers.CurrentActivityIsInvokeAgent ? Activity.Current : null;
        ActivitySource? source = invokeAgentActivity?.Source ?? _activitySource;

        using Activity? activity = source?.StartActivity(
            $"{GenAI.OperationNameValues.ExecuteToolName} {context.Function.Name}",
            ActivityKind.Internal,
            default(ActivityContext),
            [
                new(GenAI.AttributeGenAiOperationName, GenAI.OperationNameValues.ExecuteToolName),
                new(GenAI.Tool.Type, GenAI.ToolTypeFunction),
                new(GenAI.Tool.Call.Id, context.CallContent.CallId),
                new(GenAI.Tool.Name, context.Function.Name),
                new(GenAI.Tool.Description, context.Function.Description),
            ]);

        long startingTimestamp = Stopwatch.GetTimestamp();

        // Determine if sensitive data logging is enabled via the delegate
        bool enableSensitiveData = activity is { IsAllDataRequested: true } && _isSensitiveDataEnabled(invokeAgentActivity);

        bool traceLoggingEnabled = _logger.IsEnabled(LogLevel.Trace);
        bool loggedInvoke = false;

        if (enableSensitiveData || traceLoggingEnabled)
        {
            string functionArguments = TelemetryHelpers.AsJson(context.Arguments, context.Function.JsonSerializerOptions);

            if (enableSensitiveData)
            {
                _ = activity?.SetTag(GenAI.Tool.Call.Arguments, functionArguments);
            }

            if (traceLoggingEnabled)
            {
                FunctionInvocationLogger.LogInvokingSensitive(_logger, context.Function.Name, functionArguments);
                loggedInvoke = true;
            }
        }

        if (!loggedInvoke && _logger.IsEnabled(LogLevel.Debug))
        {
            FunctionInvocationLogger.LogInvoking(_logger, context.Function.Name);
        }

        object? result = null;
        try
        {
            result = await _invokeFunction(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (activity is not null)
            {
                _ = activity.SetTag(Error.AttributeErrorType, e.GetType().FullName)
                            .SetStatus(ActivityStatusCode.Error, e.Message);
            }

            if (e is OperationCanceledException)
            {
                FunctionInvocationLogger.LogInvocationCanceled(_logger, context.Function.Name);
            }
            else
            {
                FunctionInvocationLogger.LogInvocationFailed(_logger, context.Function.Name, e);
            }

            throw;
        }
        finally
        {
            bool loggedResult = false;
            if (enableSensitiveData || traceLoggingEnabled)
            {
                string functionResult = TelemetryHelpers.AsJson(result, context.Function.JsonSerializerOptions);

                if (enableSensitiveData)
                {
                    _ = activity?.SetTag(GenAI.Tool.Call.Result, functionResult);
                }

                if (traceLoggingEnabled)
                {
                    FunctionInvocationLogger.LogInvocationCompletedSensitive(_logger, context.Function.Name, FunctionInvocationHelpers.GetElapsedTime(startingTimestamp), functionResult);
                    loggedResult = true;
                }
            }

            if (!loggedResult && _logger.IsEnabled(LogLevel.Debug))
            {
                FunctionInvocationLogger.LogInvocationCompleted(_logger, context.Function.Name, FunctionInvocationHelpers.GetElapsedTime(startingTimestamp));
            }
        }

        return result;
    }
}
internal static class TelemetryHelpers
{

    public const string DefaultSourceName = "Showcase.AI";
    public const string GenAICaptureMessageContentEnvVar = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    /// <summary>Gets a value indicating whether the OpenTelemetry clients should enable their EnableSensitiveData property's by default.</summary>
    /// <remarks>Defaults to false. May be overridden by setting the OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT environment variable to "true".</remarks>
    public static bool EnableSensitiveDataDefault { get; } =
        Environment.GetEnvironmentVariable(GenAICaptureMessageContentEnvVar) is string envVar &&
        string.Equals(envVar, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Serializes <paramref name="value"/> as JSON for logging purposes.</summary>
    public static string AsJson<T>(T value, JsonSerializerOptions? options)
    {
        if (options?.TryGetTypeInfo(typeof(T), out var typeInfo) is true ||
            AIJsonUtilities.DefaultOptions.TryGetTypeInfo(typeof(T), out typeInfo))
        {
            try
            {
                return JsonSerializer.Serialize(value, typeInfo);
            }
            catch
            {
                // If we fail to serialize, just fall through to returning "{}".
            }
        }

        // If we're unable to get a type info for the value, or if we fail to serialize,
        // return an empty JSON object. We do not want lack of type info to disrupt application behavior with exceptions.
        return "{}";
    }
}
