using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.ContactCenter.IvrWorkflow;

/// <summary>
/// Authentication level for the customer.
/// </summary>
public enum AuthenticationLevel
{
    /// <summary>
    /// No authentication performed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Phone number recognized from caller ID.
    /// </summary>
    PhoneRecognized = 1,

    /// <summary>
    /// Account verified via basic information (name, account number).
    /// </summary>
    AccountVerified = 2,

    /// <summary>
    /// Security question passed.
    /// </summary>
    SecurityQuestionPassed = 3,

    /// <summary>
    /// Fully authenticated via multi-factor authentication.
    /// </summary>
    FullyAuthenticated = 4
}
