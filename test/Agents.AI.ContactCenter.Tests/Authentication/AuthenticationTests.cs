using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.Extensions.ToolApproval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.Authentication;

public class AuthenticationOrchestratorTests
{
    [Fact]
    public async Task RunsChain_ShortCircuitsOnFailed()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var orchestrator = new AuthenticationOrchestrator(
        [
            new StubAuthenticator("A", new AuthenticationOutcome.NotApplicable("skip")),
            new StubAuthenticator("B", new AuthenticationOutcome.Failed("nope")),
            new StubAuthenticator("C", new AuthenticationOutcome.Authenticated(MakeIdentity("u1", CallerVerificationLevel.AniMatch))),
        ]);

        var result = await orchestrator.AuthenticateAsync(
            new AuthenticationContext("call-1", null, state.Identity, services),
            state);

        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("B", result.Steps[^1].AuthenticatorName);
        Assert.Equal(CallerVerificationLevel.None, state.Identity.VerificationLevel);
    }

    [Fact]
    public async Task NotApplicable_FallsThroughToAuthenticated()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var orchestrator = new AuthenticationOrchestrator(
        [
            new StubAuthenticator("A", new AuthenticationOutcome.NotApplicable("skip")),
            new StubAuthenticator("B", new AuthenticationOutcome.Authenticated(MakeIdentity("u1", CallerVerificationLevel.AniMatch))),
        ]);

        var result = await orchestrator.AuthenticateAsync(
            new AuthenticationContext("call-1", null, state.Identity, services),
            state);

        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(CallerVerificationLevel.AniMatch, state.Identity.VerificationLevel);
        Assert.Equal("u1", state.Identity.UserId);
    }

    [Fact]
    public async Task NeedsChallenge_StopsAndRecordsPendingChallenge()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var challenge = new AuthenticationChallenge(
            AuthenticationMethod.SmsOtp, "Enter code", "ch-1", DateTimeOffset.UtcNow.AddMinutes(5));
        var orchestrator = new AuthenticationOrchestrator(
        [
            new StubAuthenticator("A", new AuthenticationOutcome.NeedsChallenge(challenge)),
            new StubAuthenticator("B", new AuthenticationOutcome.Authenticated(MakeIdentity("u1", CallerVerificationLevel.AniMatch))),
        ]);

        var result = await orchestrator.AuthenticateAsync(
            new AuthenticationContext("call-1", null, state.Identity, services),
            state);

        Assert.Single(result.Steps);
        Assert.Same(challenge, state.PendingChallenge);
    }

    [Fact]
    public async Task EmptyAuthenticatorList_ReturnsAnonymous()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var orchestrator = new AuthenticationOrchestrator([]);

        var result = await orchestrator.AuthenticateAsync(
            new AuthenticationContext("call-1", null, state.Identity, services),
            state);

        Assert.Empty(result.Steps);
        Assert.Equal(CallerIdentity.Anonymous, result.Identity);
    }

    internal static CallerIdentity MakeIdentity(string userId, CallerVerificationLevel level) => new(
        UserId: userId,
        DisplayName: userId,
        PhoneNumber: "+15551234567",
        Email: null,
        EntraObjectId: null,
        VerificationLevel: level,
        AuthenticatedAt: DateTimeOffset.UtcNow,
        AuthenticatedBy: "test",
        Claims: new Dictionary<string, object?>());

    internal sealed class StubAuthenticator : ICallerAuthenticator
    {
        private readonly AuthenticationOutcome _outcome;
        public StubAuthenticator(string name, AuthenticationOutcome outcome)
        {
            Name = name;
            _outcome = outcome;
        }
        public string Name { get; }
        public Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(_outcome);
    }
}

public class CallerElevationDispatcherTests
{
    [Fact]
    public async Task Dispatch_EmitsLevelChangedAndIdentified_ExactlyOnce()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var dispatcher = new CallerElevationDispatcher(
        [
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "Pin",
                new AuthenticationOutcome.Authenticated(AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.KnowledgeBased))),
        ],
            state, services);

        var channel = Channel.CreateUnbounded<StrategyEvent>();
        await dispatcher.DispatchAsync("Pin", "call-1", events: channel.Writer);
        channel.Writer.Complete();

        var events = await channel.Reader.ReadAllAsync().ToListAsync();
        Assert.Single(events.OfType<StrategyEvent.CallerVerificationLevelChanged>());
        Assert.Single(events.OfType<StrategyEvent.CallerIdentified>());
        Assert.Equal(CallerVerificationLevel.KnowledgeBased, state.Identity.VerificationLevel);
    }

    [Fact]
    public async Task Dispatch_UnknownAuthenticator_IsNoOp()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var dispatcher = new CallerElevationDispatcher([], state, services);

        var result = await dispatcher.DispatchAsync("Missing", "call-1");
        Assert.Empty(result.Steps);
        Assert.Equal(CallerIdentity.Anonymous, state.Identity);
    }

    [Fact]
    public async Task Dispatch_Failed_EmitsAuthenticationFailedNoLevelChange()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var state = new CallerAuthenticationState();
        var dispatcher = new CallerElevationDispatcher(
        [
            new AuthenticationOrchestratorTests.StubAuthenticator("Pin", new AuthenticationOutcome.Failed("bad pin")),
        ],
            state, services);

        var channel = Channel.CreateUnbounded<StrategyEvent>();
        await dispatcher.DispatchAsync("Pin", "call-1", events: channel.Writer);
        channel.Writer.Complete();

        var events = await channel.Reader.ReadAllAsync().ToListAsync();
        Assert.Empty(events.OfType<StrategyEvent.CallerVerificationLevelChanged>());
        Assert.Single(events.OfType<StrategyEvent.CallerAuthenticationFailed>());
    }
}

public class PinAuthenticatorTests
{
    [Fact]
    public async Task PinAuthenticator_NotApplicable_WhenAnonymous()
    {
        var services = new ServiceCollection()
            .AddSingleton(new PinAttempt { Digits = "1234" })
            .AddSingleton<IPinValidator>(new FakeValidator(true))
            .BuildServiceProvider();
        var auth = new PinAuthenticator();
        var outcome = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, CallerIdentity.Anonymous, services));
        Assert.IsType<AuthenticationOutcome.Failed>(outcome);
    }

    [Fact]
    public async Task PinAuthenticator_Authenticated_OnMatch()
    {
        var attempt = new PinAttempt { Digits = "4242" };
        var services = new ServiceCollection()
            .AddSingleton(attempt)
            .AddSingleton<IPinValidator>(new FakeValidator(true))
            .BuildServiceProvider();
        var auth = new PinAuthenticator();
        var identity = AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.AniMatch);
        var outcome = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));

        var authenticated = Assert.IsType<AuthenticationOutcome.Authenticated>(outcome);
        Assert.Equal(CallerVerificationLevel.KnowledgeBased, authenticated.Identity.VerificationLevel);
        Assert.Null(attempt.Digits);
    }

    [Fact]
    public async Task PinAuthenticator_Failed_OnMismatch()
    {
        var services = new ServiceCollection()
            .AddSingleton(new PinAttempt { Digits = "0000" })
            .AddSingleton<IPinValidator>(new FakeValidator(false))
            .BuildServiceProvider();
        var auth = new PinAuthenticator();
        var identity = AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.AniMatch);
        var outcome = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));
        Assert.IsType<AuthenticationOutcome.Failed>(outcome);
    }

    [Fact]
    public async Task PinAuthenticator_NotApplicable_WhenNoAttempt()
    {
        var services = new ServiceCollection()
            .AddSingleton<IPinValidator>(new FakeValidator(true))
            .BuildServiceProvider();
        var auth = new PinAuthenticator();
        var identity = AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.AniMatch);
        var outcome = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));
        Assert.IsType<AuthenticationOutcome.NotApplicable>(outcome);
    }

    private sealed class FakeValidator : IPinValidator
    {
        private readonly bool? _result;
        public FakeValidator(bool? result) { _result = result; }
        public Task<bool?> ValidateAsync(CallerIdentity identity, string digits, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }
}

public class SmsOtpAuthenticatorTests
{
    [Fact]
    public async Task SmsOtp_Phase1_IssuesChallengeAndSaves()
    {
        var store = new InMemoryChallengeStore();
        var sender = new RecordingSender();
        var services = new ServiceCollection()
            .AddSingleton<IChallengeStore>(store)
            .AddSingleton<ISmsOtpSender>(sender)
            .BuildServiceProvider();

        var auth = new SmsOtpAuthenticator();
        var identity = AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.AniMatch);
        var outcome = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));

        var challenge = Assert.IsType<AuthenticationOutcome.NeedsChallenge>(outcome).Challenge;
        Assert.Single(sender.Sent);
        var saved = await store.GetAsync(challenge.ChallengeId);
        Assert.NotNull(saved);
        Assert.Equal(sender.Sent[0].code, saved!.Secret);
    }

    [Fact]
    public async Task SmsOtp_Phase2_ValidatesAndElevates()
    {
        var store = new InMemoryChallengeStore();
        var sender = new RecordingSender();
        var attempt = new SmsOtpAttempt();
        var services = new ServiceCollection()
            .AddSingleton<IChallengeStore>(store)
            .AddSingleton<ISmsOtpSender>(sender)
            .AddSingleton(attempt)
            .BuildServiceProvider();

        var auth = new SmsOtpAuthenticator();
        var identity = AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.AniMatch);

        var phase1 = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));
        var challenge = Assert.IsType<AuthenticationOutcome.NeedsChallenge>(phase1).Challenge;

        attempt.ChallengeId = challenge.ChallengeId;
        attempt.Code = sender.Sent[0].code;

        var phase2 = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));
        var authenticated = Assert.IsType<AuthenticationOutcome.Authenticated>(phase2);
        Assert.Equal(CallerVerificationLevel.MultiFactor, authenticated.Identity.VerificationLevel);

        // Store entry consumed.
        Assert.Null(await store.GetAsync(challenge.ChallengeId));
    }

    [Fact]
    public async Task SmsOtp_Phase2_FailedOnWrongCode()
    {
        var store = new InMemoryChallengeStore();
        var sender = new RecordingSender();
        var attempt = new SmsOtpAttempt();
        var services = new ServiceCollection()
            .AddSingleton<IChallengeStore>(store)
            .AddSingleton<ISmsOtpSender>(sender)
            .AddSingleton(attempt)
            .BuildServiceProvider();

        var auth = new SmsOtpAuthenticator();
        var identity = AuthenticationOrchestratorTests.MakeIdentity("u1", CallerVerificationLevel.AniMatch);
        var phase1 = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));
        var challenge = Assert.IsType<AuthenticationOutcome.NeedsChallenge>(phase1).Challenge;

        attempt.ChallengeId = challenge.ChallengeId;
        attempt.Code = "000000";

        var phase2 = await auth.AuthenticateAsync(new AuthenticationContext("c1", null, identity, services));
        Assert.IsType<AuthenticationOutcome.Failed>(phase2);
    }

    private sealed class RecordingSender : ISmsOtpSender
    {
        public List<(string phone, string code)> Sent { get; } = [];
        public Task SendAsync(string phoneNumberE164, string code, CancellationToken cancellationToken = default)
        {
            Sent.Add((phoneNumberE164, code));
            return Task.CompletedTask;
        }
    }
}

public class RequiresCallerVerificationAttributeTests
{
    [Fact]
    public void HoldsMinimumLevelAndFailureMessage()
    {
        var attr = new RequiresCallerVerificationAttribute(CallerVerificationLevel.MultiFactor) { FailureMessage = "go away" };
        Assert.Equal(CallerVerificationLevel.MultiFactor, attr.MinimumLevel);
        Assert.Equal("go away", attr.FailureMessage);
    }

    [Fact]
    public void FailureMessageIsOptional()
    {
        var attr = new RequiresCallerVerificationAttribute(CallerVerificationLevel.AniMatch);
        Assert.Equal(CallerVerificationLevel.AniMatch, attr.MinimumLevel);
        Assert.Null(attr.FailureMessage);
    }
}
