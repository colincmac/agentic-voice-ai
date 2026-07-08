using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.ContactCenter.Authentication;


/// <summary>
/// Post-run promotion: when the caller has cleared <paramref name="requiredFactors"/>
/// distinct successful authenticators in <see cref="CallerAuthenticationState.Steps"/>,
/// elevate the identity to <see cref="CallerVerificationLevel.Strong"/>.
/// </summary>
public sealed class CompositeStrongAuthenticationLevel(int requiredFactors = 3)
{
    public void Apply(CallerAuthenticationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var distinctSuccesses = state.Steps
            .Where(s => s.Outcome is AuthenticationOutcome.Authenticated)
            .Select(s => s.AuthenticatorName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctSuccesses >= requiredFactors
            && state.Identity.VerificationLevel < CallerVerificationLevel.Strong)
        {
            state.TryPromote(state.Identity with
            {
                VerificationLevel = CallerVerificationLevel.Strong,
                AuthenticatedAt = DateTimeOffset.UtcNow,
                AuthenticatedBy = "Composite",
            });
        }
    }
}
