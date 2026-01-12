using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace Agents.AI.RealtimeVoice.Azure.Authorization;

public interface IUserIdentityService
{
    Task<UserIdentity?> LookupUserAsync(string name, string phoneNumber, CancellationToken cancellationToken = default);
    Task<MfaVerificationSession> InitiateMfaAsync(string userId, string participantId, MfaMethod method, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyMfaChallengeAsync(string sessionId, string challengeResponse, CancellationToken cancellationToken = default);
    Task<bool> SendMfaChallengeAsync(MfaVerificationSession session, CancellationToken cancellationToken = default);
    Task SeedDemoDataAsync();
}
public class InMemoryUserIdentityService : IUserIdentityService
{
    private readonly ConcurrentDictionary<string, UserIdentity> _users = new();
    private readonly ConcurrentDictionary<string, MfaVerificationSession> _verificationSessions = new();
    private readonly ILogger<InMemoryUserIdentityService> _logger;
    private readonly IPublicClientApplication? _msalClient;
    private readonly IHttpClientFactory _httpClientFactory;

    public InMemoryUserIdentityService(
        ILogger<InMemoryUserIdentityService> logger,
        IHttpClientFactory httpClientFactory,

        IPublicClientApplication? msalClient = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _msalClient = msalClient;

        // Seed demo data
        _ = SeedDemoDataAsync();
    }

    public async Task SeedDemoDataAsync()
    {
        var demoUsers = new[]
        {
            new UserIdentity
            {
                UserId = "user001",
                EntraObjectId = "00000000-0000-0000-0000-000000000001",
                UserPrincipalName = "john.doe@contoso.com",
                FirstName = "John",
                LastName = "Doe",
                Email = "john.doe@contoso.com",
                PhoneNumber = "+14255551234",
                AlternatePhoneNumber = "+14255555678",
                RegisteredMfaMethods = [
                    MfaMethod.MicrosoftAuthenticator.ToString(),
                    MfaMethod.SmsOtp.ToString(),
                    MfaMethod.MagicLink.ToString()
                ],
                Claims = new()
                {
                    ["department"] = "Engineering",
                    ["clearanceLevel"] = "3",
                    ["accountTier"] = "Premium"
                }
            },
            new UserIdentity
            {
                UserId = "user002",
                EntraObjectId = "00000000-0000-0000-0000-000000000002",
                UserPrincipalName = "jane.smith@contoso.com",
                FirstName = "Jane",
                LastName = "Smith",
                Email = "jane.smith@contoso.com",
                PhoneNumber = "+14255559876",
                RegisteredMfaMethods = [
                    MfaMethod.SmsOtp.ToString(),
                    MfaMethod.Email.ToString()
                ],
                Claims = new()
                {
                    ["department"] = "Finance",
                    ["clearanceLevel"] = "2",
                    ["accountTier"] = "Standard"
                }
            },
            new UserIdentity
            {
                UserId = "user003",
                FirstName = "Bob",
                LastName = "Wilson",
                Email = "bob.wilson@contoso.com",
                PhoneNumber = "+14255553456",
                RegisteredMfaMethods = [
                    MfaMethod.SmsOtp.ToString()
                ],
                Claims = new()
                {
                    ["department"] = "Support",
                    ["clearanceLevel"] = "1",
                    ["accountTier"] = "Basic"
                }
            }
        };

        foreach (var user in demoUsers)
        {
            _users[user.UserId] = user;
        }

        _logger.LogInformation("Seeded {Count} demo users", demoUsers.Length);
        await Task.CompletedTask;
    }

    public Task<UserIdentity?> LookupUserAsync(string name, string phoneNumber, CancellationToken cancellationToken = default)
    {
        // Normalize phone number
        var normalizedPhone = NormalizePhoneNumber(phoneNumber);
        var nameParts = name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var user = _users.Values.FirstOrDefault(u =>
        {
            // Check phone number match
            var phoneMatch = NormalizePhoneNumber(u.PhoneNumber) == normalizedPhone ||
                            NormalizePhoneNumber(u.AlternatePhoneNumber) == normalizedPhone;

            if (!phoneMatch) return false;

            // Check name match (fuzzy)
            var firstNameMatch = nameParts.Any(part =>
                u.FirstName?.ToLowerInvariant().Contains(part) == true);
            var lastNameMatch = nameParts.Any(part =>
                u.LastName?.ToLowerInvariant().Contains(part) == true);

            return firstNameMatch || lastNameMatch;
        });

        if (user != null)
        {
            _logger.LogInformation(
                "Found user {UserId} for name '{Name}' and phone '{Phone}'",
                user.UserId, name, phoneNumber);
        }
        else
        {
            _logger.LogWarning(
                "No user found for name '{Name}' and phone '{Phone}'",
                name, phoneNumber);
        }

        return Task.FromResult(user);
    }

    public async Task<MfaVerificationSession> InitiateMfaAsync(
        string userId,
        string participantId,
        MfaMethod method,
        CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(userId, out var user))
        {
            throw new ArgumentException($"User {userId} not found");
        }

        // Check if method is registered for user
        if (!user.RegisteredMfaMethods.Contains(method.ToString()))
        {
            throw new InvalidOperationException($"MFA method {method} not registered for user {userId}");
        }

        // Cancel any existing sessions for this participant
        var existingSessions = _verificationSessions.Values
            .Where(s => s.ParticipantId == participantId && s.Status == VerificationStatus.Initiated)
            .ToList();

        foreach (var oldSession in existingSessions)
        {
            oldSession.Status = VerificationStatus.Cancelled;
        }

        // Create new session
        var session = new MfaVerificationSession
        {
            UserId = userId,
            ParticipantId = participantId,
            Method = method,
            Status = VerificationStatus.Initiated,
            ChallengeCode = GenerateChallengeCode(method),
            VerificationToken = GenerateVerificationToken()
        };

        _verificationSessions[session.SessionId] = session;

        // Send the challenge
        await SendMfaChallengeAsync(session, cancellationToken);

        _logger.LogInformation(
            "Initiated MFA session {SessionId} for user {UserId} using {Method}",
            session.SessionId, userId, method);

        return session;
    }

    public async Task<bool> SendMfaChallengeAsync(
        MfaVerificationSession session,
        CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(session.UserId, out var user))
        {
            return false;
        }

        try
        {
            switch (session.Method)
            {
                //case MfaMethod.SmsOtp:
                //    await SendSmsOtpAsync(user, session, cancellationToken);
                //    break;

                //case MfaMethod.Email:
                //    await SendEmailOtpAsync(user, session, cancellationToken);
                //    break;

                //case MfaMethod.MagicLink:
                //    await SendMagicLinkAsync(user, session, cancellationToken);
                //    break;

                case MfaMethod.MicrosoftAuthenticator:
                    await InitiateMicrosoftAuthenticatorAsync(user, session, cancellationToken);
                    break;

                case MfaMethod.PhoneCall:
                    // For phone call, we might speak the code
                    session.Metadata["spokenCode"] = FormatCodeForSpeech(session.ChallengeCode);
                    break;
            }

            session.Status = VerificationStatus.Challenged;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send MFA challenge for session {SessionId}",
                session.SessionId);
            return false;
        }
    }

    public async Task<VerificationResult> VerifyMfaChallengeAsync(
        string sessionId,
        string challengeResponse,
        CancellationToken cancellationToken = default)
    {
        if (!_verificationSessions.TryGetValue(sessionId, out var session))
        {
            return new VerificationResult
            {
                Success = false,
                Status = VerificationStatus.Failed,
                ErrorMessage = "Verification session not found"
            };
        }

        // Check expiration
        if (DateTimeOffset.UtcNow > session.ExpiresAt)
        {
            session.Status = VerificationStatus.Expired;
            return new VerificationResult
            {
                Success = false,
                Status = VerificationStatus.Expired,
                ErrorMessage = "Verification session expired"
            };
        }

        session.AttemptCount++;

        // Verify based on method
        bool isValid = session.Method switch
        {
            MfaMethod.SmsOtp or MfaMethod.Email or MfaMethod.PhoneCall =>
                VerifyOtpCode(session.ChallengeCode, challengeResponse),

            MfaMethod.MagicLink =>
                await VerifyMagicLinkTokenAsync(session, challengeResponse, cancellationToken),

            MfaMethod.MicrosoftAuthenticator =>
                await VerifyMicrosoftAuthenticatorAsync(session, challengeResponse, cancellationToken),

            _ => false
        };

        if (isValid)
        {
            session.Status = VerificationStatus.Verified;
            session.VerifiedAt = DateTimeOffset.UtcNow;

            _users.TryGetValue(session.UserId, out var user);
            if (user != null)
            {
                user.LastVerified = session.VerifiedAt;
            }

            _logger.LogInformation(
                "Successfully verified MFA session {SessionId} for user {UserId}",
                session.SessionId, session.UserId);

            return new VerificationResult
            {
                Success = true,
                Status = VerificationStatus.Verified,
                VerifiedIdentity = user,
                VerifiedAt = session.VerifiedAt,
                Claims = user?.Claims ?? new()
            };
        }

        // Check max attempts
        if (session.AttemptCount >= 3)
        {
            session.Status = VerificationStatus.Failed;
            return new VerificationResult
            {
                Success = false,
                Status = VerificationStatus.Failed,
                ErrorMessage = "Maximum verification attempts exceeded"
            };
        }

        return new VerificationResult
        {
            Success = false,
            Status = session.Status,
            ErrorMessage = "Invalid verification code"
        };
    }

    //private async Task SendSmsOtpAsync(UserIdentity user, MfaVerificationSession session, CancellationToken cancellationToken)
    //{
    //    var message = $"Your verification code is: {session.ChallengeCode}. Valid for 5 minutes.";

    //    if (_smsClient != null && !string.IsNullOrEmpty(user.PhoneNumber))
    //    {
    //        await _smsClient.SendAsync(
    //            from: "+18885551234", // Your SMS number
    //            to: user.PhoneNumber,
    //            message: message,
    //            cancellationToken: cancellationToken);
    //    }
    //    else
    //    {
    //        // In demo mode, log the code
    //        _logger.LogInformation("SMS OTP for {Phone}: {Code}", user.PhoneNumber, session.ChallengeCode);
    //    }
    //}

    //private async Task SendEmailOtpAsync(UserIdentity user, MfaVerificationSession session, CancellationToken cancellationToken)
    //{
    //    var subject = "Your Verification Code";
    //    var body = $@"
    //        <html>
    //            <body>
    //                <h2>Verification Required</h2>
    //                <p>Your verification code is: <strong>{session.ChallengeCode}</strong></p>
    //                <p>This code will expire in 5 minutes.</p>
    //            </body>
    //        </html>";

    //    if (_emailClient != null && !string.IsNullOrEmpty(user.Email))
    //    {
    //        await _emailClient.SendAsync(
    //            wait: Azure.WaitUntil.Started,
    //            senderAddress: "noreply@contoso.com",
    //            recipientAddress: user.Email,
    //            subject: subject,
    //            htmlContent: body,
    //            cancellationToken: cancellationToken);
    //    }
    //    else
    //    {
    //        _logger.LogInformation("Email OTP for {Email}: {Code}", user.Email, session.ChallengeCode);
    //    }
    //}

    //private async Task SendMagicLinkAsync(UserIdentity user, MfaVerificationSession session, CancellationToken cancellationToken)
    //{
    //    var magicLink = $"https://verify.contoso.com/auth?token={session.VerificationToken}&session={session.SessionId}";
    //    var message = $"Tap this link to verify your identity: {magicLink}";

    //    if (_smsClient != null && !string.IsNullOrEmpty(user.PhoneNumber))
    //    {
    //        await _smsClient.SendAsync(
    //            from: "+18885551234",
    //            to: user.PhoneNumber,
    //            message: message,
    //            cancellationToken: cancellationToken);
    //    }
    //    else
    //    {
    //        _logger.LogInformation("Magic link for {Phone}: {Link}", user.PhoneNumber, magicLink);
    //        // In demo mode, auto-verify after a delay
    //        _ = Task.Run(async () =>
    //        {
    //            await Task.Delay(3000, cancellationToken);
    //            session.Metadata["magicLinkClicked"] = true;
    //        }, cancellationToken);
    //    }

    //    await Task.CompletedTask;
    //}

    private async Task InitiateMicrosoftAuthenticatorAsync(UserIdentity user, MfaVerificationSession session, CancellationToken cancellationToken)
    {
        if (_msalClient != null && !string.IsNullOrEmpty(user.UserPrincipalName))
        {
            // In production, this would trigger a push notification to Microsoft Authenticator
            session.Metadata["authenticatorRequestId"] = Guid.NewGuid().ToString();
            _logger.LogInformation(
                "Initiated Microsoft Authenticator push for {UPN}",
                user.UserPrincipalName);
        }
        else
        {
            // Demo mode
            _logger.LogInformation("Microsoft Authenticator request for {UPN}", user.UserPrincipalName);
            session.Metadata["authenticatorDemo"] = true;
        }

        await Task.CompletedTask;
    }

    private bool VerifyOtpCode(string? expected, string provided)
    {
        if (string.IsNullOrEmpty(expected)) return false;

        // Remove spaces and normalize
        var normalizedExpected = expected.Replace(" ", "").Replace("-", "");
        var normalizedProvided = provided.Replace(" ", "").Replace("-", "");

        return string.Equals(normalizedExpected, normalizedProvided, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> VerifyMagicLinkTokenAsync(MfaVerificationSession session, string token, CancellationToken cancellationToken)
    {
        // In demo mode, check if link was "clicked"
        if (session.Metadata.TryGetValue("magicLinkClicked", out var clicked) && clicked is true)
        {
            return true;
        }

        // In production, verify the token
        return session.VerificationToken == token;
    }

    private async Task<bool> VerifyMicrosoftAuthenticatorAsync(MfaVerificationSession session, string response, CancellationToken cancellationToken)
    {
        // In demo mode
        if (session.Metadata.TryGetValue("authenticatorDemo", out var demo) && demo is true)
        {
            // Accept "approved" as the response
            return response.ToLowerInvariant() == "approved";
        }

        // In production, check with Azure AD
        if (_msalClient != null && session.Metadata.TryGetValue("authenticatorRequestId", out var requestId))
        {
            // Check authentication status
            // This would involve calling Azure AD APIs
            return true;
        }

        return false;
    }

    private string GenerateChallengeCode(MfaMethod method)
    {
        return method switch
        {
            MfaMethod.SmsOtp or MfaMethod.Email or MfaMethod.PhoneCall =>
                GenerateNumericCode(6),
            _ => Guid.NewGuid().ToString("N")[..8]
        };
    }

    private string GenerateNumericCode(int length)
    {
        var random = new Random();
        var code = "";
        for (int i = 0; i < length; i++)
        {
            code += random.Next(0, 10);
        }
        return code;
    }

    private string GenerateVerificationToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private string FormatCodeForSpeech(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        return string.Join(" ", code.ToCharArray());
    }

    private string NormalizePhoneNumber(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "";
        return new string(phone.Where(char.IsDigit).ToArray());
    }
}
