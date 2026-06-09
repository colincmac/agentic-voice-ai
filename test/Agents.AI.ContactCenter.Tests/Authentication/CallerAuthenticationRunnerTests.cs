using System.Threading.Channels;
using global::Agents.AI.ContactCenter.Authentication;
using global::Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.Tests.Authentication;

public sealed class CallerAuthenticationRunnerTests
{
    [Fact]
    public async Task NoOrchestratorRegistered_ReturnsAnonymousAndEmitsNothing()
    {
        var services = new ServiceCollection()
            .AddSingleton<CallerAuthenticationState>()
            .BuildServiceProvider();
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services),
            services,
            events.Writer);

        Assert.Empty(result.Steps);
        Assert.Same(CallerIdentity.Anonymous, result.Identity);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NoStateRegistered_ReturnsAnonymousAndEmitsNothing()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationOrchestrator>(_ => new AuthenticationOrchestrator([]))
            .BuildServiceProvider();
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services),
            services,
            events.Writer);

        Assert.Empty(result.Steps);
        Assert.Same(CallerIdentity.Anonymous, result.Identity);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AniMatch_PromotesStateAndEmitsCallerIdentifiedThenLevelChanged()
    {
        var identity = AuthenticationOrchestratorTests.MakeIdentity("cust-1", CallerVerificationLevel.AniMatch);
        var state = new CallerAuthenticationState();
        var services = BuildServices(state,
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "AniLookup", new AuthenticationOutcome.Authenticated(identity)));
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services),
            services,
            events.Writer);

        Assert.Equal(CallerVerificationLevel.AniMatch, state.Identity.VerificationLevel);
        Assert.Equal("cust-1", state.Identity.UserId);
        Assert.Equal(CallerVerificationLevel.AniMatch, result.Identity.VerificationLevel);

        events.Writer.Complete();
        var emitted = await DrainAsync(events.Reader);
        Assert.Collection(emitted,
            e =>
            {
                var identified = Assert.IsType<StrategyEvent.CallerIdentified>(e);
                Assert.Equal("cust-1", identified.Identity.UserId);
                Assert.Equal("AniLookup", identified.AuthenticatorName);
            },
            e =>
            {
                var changed = Assert.IsType<StrategyEvent.CallerVerificationLevelChanged>(e);
                Assert.Equal(CallerVerificationLevel.None, changed.From);
                Assert.Equal(CallerVerificationLevel.AniMatch, changed.To);
            });
    }

    [Fact]
    public async Task Failed_EmitsCallerAuthenticationFailedAndNoLevelChanged()
    {
        var state = new CallerAuthenticationState();
        var services = BuildServices(state,
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "AniLookup", new AuthenticationOutcome.Failed("no record")));
        var events = Channel.CreateUnbounded<StrategyEvent>();

        await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services,
            events.Writer);

        events.Writer.Complete();
        var emitted = await DrainAsync(events.Reader);
        var failed = Assert.Single(emitted);
        var failedEvent = Assert.IsType<StrategyEvent.CallerAuthenticationFailed>(failed);
        Assert.Equal("AniLookup", failedEvent.AuthenticatorName);
        Assert.Equal("no record", failedEvent.Reason);
        Assert.Equal(CallerVerificationLevel.None, state.Identity.VerificationLevel);
    }

    [Fact]
    public async Task NeedsChallenge_EmitsCallerAuthenticationChallengeAndNoLevelChanged()
    {
        var challenge = new AuthenticationChallenge(
            AuthenticationMethod.SmsOtp,
            "Enter the 6-digit code",
            "ch-1",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var state = new CallerAuthenticationState();
        var services = BuildServices(state,
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "SmsOtp", new AuthenticationOutcome.NeedsChallenge(challenge)));
        var events = Channel.CreateUnbounded<StrategyEvent>();

        await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services,
            events.Writer);

        events.Writer.Complete();
        var emitted = await DrainAsync(events.Reader);
        var single = Assert.Single(emitted);
        var challengeEvent = Assert.IsType<StrategyEvent.CallerAuthenticationChallenge>(single);
        Assert.Same(challenge, challengeEvent.Challenge);
        Assert.Same(challenge, state.PendingChallenge);
    }

    [Fact]
    public async Task NullEventsChannel_StillRunsOrchestratorAndPromotesState()
    {
        var identity = AuthenticationOrchestratorTests.MakeIdentity("cust-1", CallerVerificationLevel.AniMatch);
        var state = new CallerAuthenticationState();
        var services = BuildServices(state,
            new AuthenticationOrchestratorTests.StubAuthenticator(
                "AniLookup", new AuthenticationOutcome.Authenticated(identity)));

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services,
            events: null);

        Assert.Equal(CallerVerificationLevel.AniMatch, state.Identity.VerificationLevel);
        Assert.Equal("cust-1", result.Identity.UserId);
    }

    [Fact]
    public async Task OrchestratorThrows_SwallowsAndReturnsCurrentIdentity()
    {
        var state = new CallerAuthenticationState();
        var services = new ServiceCollection()
            .AddSingleton(state)
            .AddSingleton<IAuthenticationOrchestrator>(_ => new ThrowingOrchestrator())
            .BuildServiceProvider();
        var events = Channel.CreateUnbounded<StrategyEvent>();

        var result = await CallerAuthenticationRunner.RunAsync(
            BuildContext(services), services,
            events.Writer);

        Assert.Empty(result.Steps);
        Assert.Same(CallerIdentity.Anonymous, result.Identity);
        Assert.Equal(CallerVerificationLevel.None, state.Identity.VerificationLevel);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CallerMetadata_IsForwardedIntoAuthenticationContext()
    {
        var state = new CallerAuthenticationState();
        var capturing = new CapturingAuthenticator();
        var services = BuildServices(state, capturing);

        var metadata = new CallEdgeMetadata
        {
            DisplayName = "Jordan Reyes",
            RawIdentifier = "4:+14123236796",
        };

        await CallerAuthenticationRunner.RunAsync(
            BuildContext(services, metadata), services);

        Assert.NotNull(capturing.SeenContext);
        Assert.Same(metadata, capturing.SeenContext!.CallerMetadata);
        Assert.Equal("call-test", capturing.SeenContext.CallId);
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CallerAuthenticationRunner.RunAsync(context: null!, services: null!));
    }

    private static IServiceProvider BuildServices(
        CallerAuthenticationState state,
        params ICallerAuthenticator[] authenticators) =>
        new ServiceCollection()
            .AddSingleton(state)
            .AddSingleton<IAuthenticationOrchestrator>(_ => new AuthenticationOrchestrator(authenticators))
            .BuildServiceProvider();

    private static StrategyStartContext BuildContext(
        IServiceProvider services,
        CallEdgeMetadata? metadata = null) => new()
        {
            CallId = "call-test",
            InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
            InboundDtmf = Channel.CreateUnbounded<DtmfTone>().Reader,
            Services = services,
            CallerMetadata = metadata,
        };

    private static async Task<List<StrategyEvent>> DrainAsync(ChannelReader<StrategyEvent> reader)
    {
        var events = new List<StrategyEvent>();
        await foreach (var evt in reader.ReadAllAsync())
        {
            events.Add(evt);
        }
        return events;
    }

    private sealed class ThrowingOrchestrator : IAuthenticationOrchestrator
    {
        public Task<AuthenticationRunResult> AuthenticateAsync(
            AuthenticationContext context,
            CallerAuthenticationState state,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CapturingAuthenticator : ICallerAuthenticator
    {
        public AuthenticationContext? SeenContext { get; private set; }
        public string Name => "Capture";
        public Task<AuthenticationOutcome> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken = default)
        {
            SeenContext = context;
            return Task.FromResult<AuthenticationOutcome>(new AuthenticationOutcome.NotApplicable("captured"));
        }
    }
}
