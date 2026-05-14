namespace Agents.AI.ContactCenter.Authentication.UserIdentity;

public class UserIdentity
{
    public string UserId { get; set; } = string.Empty;
    public string? EntraObjectId { get; set; }
    public string? UserPrincipalName { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? AlternatePhoneNumber { get; set; }
    public DateTimeOffset? LastVerified { get; set; }
    public List<string> RegisteredMfaMethods { get; set; } = new();
    public Dictionary<string, object> Claims { get; set; } = new();
}

public enum MfaMethod
{
    MicrosoftAuthenticator,
    SmsOtp,
    PhoneCall,
    Email,
    MagicLink
}

public enum VerificationStatus
{
    Pending,
    Initiated,
    Challenged,
    Verified,
    Failed,
    Expired,
    Cancelled
}

public class MfaVerificationSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = string.Empty;
    public string ParticipantId { get; set; } = string.Empty;
    public MfaMethod Method { get; set; }
    public VerificationStatus Status { get; set; } = VerificationStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
    public string? ChallengeCode { get; set; }
    public string? VerificationToken { get; set; }
    public int AttemptCount { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class VerificationResult
{
    public bool Success { get; set; }
    public VerificationStatus Status { get; set; }
    public UserIdentity? VerifiedIdentity { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Dictionary<string, object> Claims { get; set; } = new();
}
