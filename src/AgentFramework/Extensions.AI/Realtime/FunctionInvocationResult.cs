using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace Microsoft.Extensions.AI;

/// <summary>Provides information about the invocation of a function call.</summary>
public sealed class FunctionInvocationResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FunctionInvocationResult"/> class.
    /// </summary>
    /// <param name="terminate">Indicates whether the caller should terminate the processing loop.</param>
    /// <param name="status">Indicates the status of the function invocation.</param>
    /// <param name="callContent">Contains information about the function call.</param>
    /// <param name="result">The result of the function call.</param>
    /// <param name="exception">The exception thrown by the function call, if any.</param>
    internal FunctionInvocationResult(bool terminate, FunctionInvocationStatus status, FunctionCallContent callContent, object? result, Exception? exception)
    {
        Terminate = terminate;
        Status = status;
        CallContent = callContent;
        Result = result;
        Exception = exception;
    }

    /// <summary>Gets status about how the function invocation completed.</summary>
    public FunctionInvocationStatus Status { get; }

    /// <summary>Gets the function call content information associated with this invocation.</summary>
    public FunctionCallContent CallContent { get; }

    /// <summary>Gets the result of the function call.</summary>
    public object? Result { get; }

    /// <summary>Gets any exception the function call threw.</summary>
    public Exception? Exception { get; }

    /// <summary>Gets a value indicating whether the caller should terminate the processing loop.</summary>
    public bool Terminate { get; }
}

/// <summary>Provides error codes for when errors occur as part of the function calling loop.</summary>
public enum FunctionInvocationStatus
{
    Rejected,
    /// <summary>The operation completed successfully.</summary>
    RanToCompletion,

    /// <summary>The requested function could not be found.</summary>
    NotFound,

    /// <summary>The function call failed with an exception.</summary>
    Exception,
}
