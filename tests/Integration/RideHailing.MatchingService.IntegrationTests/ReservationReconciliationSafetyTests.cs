// Reservation-reconciliation safety repair (L1/L2).
//
// Every scenario uses REAL PostgreSQL on both sides: the Driver authority is
// the production DriverRepository (reserve/query/lifecycle), the Matching
// ledger is the production MatchingAttemptRepository/orchestrator, and the
// transport-ambiguity scenarios inject a controlled HTTP failure at the real
// Kestrel boundary (DriverAuthorityHost), never a status-code stub alone.
//
// Safety property under proof: an existing or potentially committed
// reservation must never be treated as a conclusive rejection unless
// identity-aware authoritative evidence proves it safe to advance.
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class ReservationReconciliationSafetyTests
{
    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;

    public ReservationReconciliationSafetyTests(
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers)
    {
        _matching = matching;
        _drivers = drivers;
    }

    private MatchingAttemptRepository Attempts() => new(_matching.ConnectionString);

    private MatchingReserveOrchestrator Orchestrator(IDriverClient client) =>
        new(Attempts(), client,
            new MatchingTerminalizer(_matching.DataSource, _matching.OutboxStore));

    private sealed class RepositoryAuthorityClient(DriverRepository repository) : IDriverClient
    {
        public int VersionReads;
        public int ReserveCalls;
        public int QueryCalls;

        public async Task<DriverVersionRead?> TryGetDriverVersionAsync(
            Guid driverId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref VersionReads);
            var driver = await repository.LoadAsync(driverId, ct);
            return driver is null
                ? null
                : new(driver.Version, DriverRepository.ToDb(driver.Status));
        }

        public async Task<DriverReserveResult> ReserveAsync(
            DriverReserveRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ReserveCalls);
            var result = await repository.TryReserveAsync(request.DriverId, request.TripId,
                request.ReservationId, request.ExpectedVersion, request.MatchingRound, ct);
            return result.Outcome switch
            {
                ReserveOutcome.Won => new(DriverReserveOutcomeKind.Reserved, null, result.Driver?.Version),
                ReserveOutcome.Replayed => new(DriverReserveOutcomeKind.Reserved, null, result.Driver?.Version),
                _ => new(DriverReserveOutcomeKind.Rejected,
                    result.ConflictCode ?? "DRIVER_NOT_AVAILABLE", null),
            };
        }

        public async Task<ReservationReconcileResult> GetReservationAsync(
            Guid driverId, Guid reservationId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref QueryCalls);
            var truth = await repository.QueryReservationAsync(driverId, reservationId, ct);
            return new ReservationReconcileResult(
                Enum.Parse<ReservationReconcileOutcome>(truth.Outcome.ToString()), truth.Version);
        }
    }

    private sealed class Scenario(
        Guid trip, int round, int ordinal,
        Guid d1, Guid d2, Guid reservationId, int fenceVersion)
    {
        public Guid Trip { get; } = trip;
        public int Round { get; } = round;
        public int Ordinal { get; } = ordinal;
        public Guid D1 { get; } = d1;
        public Guid D2 { get; } = d2;
        public Guid ReservationId { get; } = reservationId;
        public int FenceVersion { get; } = fenceVersion;
    }

    private async Task<(Guid DriverId, int Version)> SeedAvailableAsync()
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await _drivers.CreateRepository().TryInsertAsync(driver));
        return (driver.DriverId, driver.Version);
    }

    /// <summary>
    /// Durable recovery precondition: a write-ahead IN_FLIGHT/UNKNOWN attempt
    /// whose reservation may already be committed, plus the original dispatch
    /// fence. When <paramref name="commit"/> is true the Driver really owns R.
    /// </summary>
    private async Task<Scenario> SeedUnresolvedAttemptAsync(
        string attemptState, bool commit, string? lifecycle = null)
    {
        var (d1, fence) = await SeedAvailableAsync();
        var (d2, _) = await SeedAvailableAsync();
        var trip = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var attempts = Attempts();
        await attempts.EnsureRoundAsync(trip, 1, Guid.NewGuid().ToString("D"));

        var ensured = await attempts.EnsureInflightAttemptAsync(trip, 1, 0, d1, reservationId);
        Assert.NotNull(ensured.Row);
        Assert.True(await attempts.TryRecordDispatchAsync(trip, 1, 0, fence));

        if (commit)
        {
            var reserved = await _drivers.CreateRepository()
                .TryReserveAsync(d1, trip, reservationId, fence, matchingRound: 1);
            Assert.Equal(ReserveOutcome.Won, reserved.Outcome);
        }

        if (attemptState == MatchingAttemptStates.Unknown)
        {
            Assert.True(await attempts.TryTransitionAsync(
                trip, 1, 0,
                [MatchingAttemptStates.InFlight], MatchingAttemptStates.Unknown,
                "RESERVE_TRANSPORT"));
        }

        if (lifecycle is not null)
        {
            Assert.Equal("Applied", (await _drivers.CreateRepository()
                .TryMarkTripStartedAsync(d1, trip, reservationId)).Disposition);
            if (lifecycle == "COMPLETED")
            {
                Assert.Equal("Applied", (await _drivers.CreateRepository()
                    .TryMarkTripCompletedAsync(d1, trip, reservationId)).Disposition);
            }
        }

        return new Scenario(trip, 1, 0, d1, d2, reservationId, fence);
    }

    private async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> DriverReservedOutboxAsync(Guid driver) => CountAsync(
        _drivers.DataSource,
        "SELECT count(*) FROM outbox_messages WHERE message_type = 'DriverReservedV1' AND aggregate_id = @driver",
        ("driver", driver.ToString("N")));

    private Task<long> LifecycleEffectsAsync(Guid driver, string phase) => CountAsync(
        _drivers.DataSource,
        "SELECT count(*) FROM driver_lifecycle_effects WHERE driver_id = @driver AND phase = @phase",
        ("driver", driver), ("phase", phase));

    private Task<long> MatchingFailedAsync(Guid trip) => CountAsync(
        _matching.DataSource,
        "SELECT count(*) FROM outbox_messages WHERE message_type = 'MatchingFailedV1' AND payload_json->>'tripId' = @trip",
        ("trip", trip.ToString("D")));

    private async Task AssertOriginalReservationWinsAsync(
        Scenario s, DriverStatus expectedFinalStatus)
    {
        var client = new RepositoryAuthorityClient(_drivers.CreateRepository());
        var orchestrator = Orchestrator(client);

        // Resume directly through the round executor: recovery must query
        // ownership before replaying the reserve or considering D2.
        var resumed = await orchestrator.ExecuteRoundAsync(
            s.Trip, s.Round, [s.D1, s.D2], Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.Reserved, resumed.Outcome);
        Assert.Equal(s.ReservationId, resumed.ReservationId);
        Assert.Equal(s.D1, resumed.DriverId);

        var attempts = await Attempts().LoadAttemptsAsync(s.Trip, s.Round);
        var winner = Assert.Single(attempts);
        Assert.Equal(s.Ordinal, winner.Ordinal);
        Assert.Equal(s.D1, winner.DriverId);
        Assert.Equal(s.ReservationId, winner.ReservationId);
        Assert.Equal(MatchingAttemptStates.Reserved, winner.State);

        var round = (await Attempts().LoadRoundAsync(s.Trip, s.Round))!;
        Assert.Equal(MatchingRoundStatuses.Reserved, round.Status);
        Assert.Equal(s.D1, round.SelectedDriverId);
        Assert.Equal(s.ReservationId, round.SelectedReservationId);
        Assert.Null(round.ResultMessageId);

        var driver = (await _drivers.CreateRepository().LoadAsync(s.D1))!;
        Assert.Equal(expectedFinalStatus, driver.Status);
        Assert.Null(driver.ReservationId);
        Assert.Null(driver.ReservedTripId);
        if (expectedFinalStatus == DriverStatus.OnTrip)
        {
            Assert.Equal(s.Trip, driver.ActiveTripId);
        }

        var second = (await _drivers.CreateRepository().LoadAsync(s.D2))!;
        Assert.Equal(DriverStatus.Available, second.Status);
        Assert.Null(second.ReservationId);
        Assert.Null(second.ReservedTripId);

        Assert.Equal(1L, await DriverReservedOutboxAsync(s.D1));
        Assert.Equal(1L, await LifecycleEffectsAsync(s.D1, "STARTED"));
        Assert.Equal(
            expectedFinalStatus == DriverStatus.Available ? 1L : 0L,
            await LifecycleEffectsAsync(s.D1, "COMPLETED"));
        Assert.Equal(0L, await MatchingFailedAsync(s.Trip));
    }

    [Fact]
    public async Task InFlight_AfterStart_SameReservation_ResolvesOwnership_WithoutD2()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.InFlight, commit: true, lifecycle: "STARTED");
        await AssertOriginalReservationWinsAsync(scenario, DriverStatus.OnTrip);
    }

    [Fact]
    public async Task Unknown_AfterStart_SameReservation_ResolvesOwnership_WithoutD2()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.Unknown, commit: true, lifecycle: "STARTED");
        await AssertOriginalReservationWinsAsync(scenario, DriverStatus.OnTrip);
    }

    [Fact]
    public async Task InFlight_AfterComplete_SameReservation_PreservesHistoricalOwnership_WithoutD2()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.InFlight, commit: true, lifecycle: "COMPLETED");
        await AssertOriginalReservationWinsAsync(scenario, DriverStatus.Available);
    }

    [Fact]
    public async Task Unknown_AfterComplete_SameReservation_NoFalseRejectionAndNoD2()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.Unknown, commit: true, lifecycle: "COMPLETED");
        await AssertOriginalReservationWinsAsync(scenario, DriverStatus.Available);
    }

    [Fact]
    public async Task OnTrip_ForDifferentIdentity_IsNeverReportedAsOwned()
    {
        var (d1, fence) = await SeedAvailableAsync();
        var (d2, _) = await SeedAvailableAsync();
        var repo = _drivers.CreateRepository();

        // D1 rides a DIFFERENT trip under a DIFFERENT reservation.
        var otherTrip = Guid.NewGuid();
        var otherReservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won,
            (await repo.TryReserveAsync(d1, otherTrip, otherReservation, fence, matchingRound: 7)).Outcome);
        Assert.Equal("Applied",
            (await repo.TryMarkTripStartedAsync(d1, otherTrip, otherReservation)).Disposition);

        // Matching still holds an unresolved attempt for the original trip/R.
        var trip = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var attempts = Attempts();
        await attempts.EnsureRoundAsync(trip, 1, Guid.NewGuid().ToString("D"));
        Assert.NotNull((await attempts.EnsureInflightAttemptAsync(trip, 1, 0, d1, reservationId)).Row);
        Assert.True(await attempts.TryRecordDispatchAsync(trip, 1, 0, fence));
        Assert.True(await attempts.TryTransitionAsync(
            trip, 1, 0, [MatchingAttemptStates.InFlight], MatchingAttemptStates.Unknown,
            "RESERVE_TRANSPORT"));

        var client = new RepositoryAuthorityClient(repo);
        var reconciled = await Orchestrator(client).ReconcileAsync(trip, 1, 0);

        // Exact identity is absent: never claim R is owned.
        Assert.NotEqual(AttemptOutcome.Reserved, reconciled.Outcome);
        Assert.Equal(ReservationReconcileOutcome.OnTrip,
            (await client.GetReservationAsync(d1, reservationId)).Outcome);
        var row = Assert.Single(await attempts.LoadAttemptsAsync(trip, 1));
        Assert.NotEqual(MatchingAttemptStates.Reserved, row.State);
        Assert.Equal(reservationId, row.ReservationId);

        // D1 is untouched by the original trip's reconciliation.
        var stillOnOther = (await repo.LoadAsync(d1))!;
        Assert.Equal(DriverStatus.OnTrip, stillOnOther.Status);
        Assert.Equal(otherTrip, stillOnOther.ActiveTripId);
        Assert.Null(stillOnOther.ReservationId);
        Assert.Equal(1L, await LifecycleEffectsAsync(d1, "STARTED"));

        // ON_TRIP without exact evidence is the canonical conclusive
        // incompatible state: D2 may now be reserved safely for the original trip.
        var resumed = await Orchestrator(client).ExecuteRoundAsync(
            trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.Reserved, resumed.Outcome);
        Assert.Equal(d2, resumed.DriverId);
        var d2Row = (await repo.LoadAsync(d2))!;
        Assert.Equal(DriverStatus.Reserved, d2Row.Status);
        Assert.Equal(trip, d2Row.ReservedTripId);
    }

    [Fact]
    public async Task ExistingReserved_SameReservation_ReplayIsIdempotent()
    {
        var (d1, fence) = await SeedAvailableAsync();
        var (d2, _) = await SeedAvailableAsync();
        var trip = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var attempts = Attempts();
        await attempts.EnsureRoundAsync(trip, 1, Guid.NewGuid().ToString("D"));
        Assert.NotNull((await attempts.EnsureInflightAttemptAsync(trip, 1, 0, d1, reservationId)).Row);
        Assert.True(await attempts.TryRecordDispatchAsync(trip, 1, 0, fence));
        Assert.Equal(ReserveOutcome.Won,
            (await _drivers.CreateRepository()
                .TryReserveAsync(d1, trip, reservationId, fence, matchingRound: 1)).Outcome);
        Assert.True(await attempts.TryTransitionAsync(
            trip, 1, 0, [MatchingAttemptStates.InFlight], MatchingAttemptStates.Reserved, null));

        var client = new RepositoryAuthorityClient(_drivers.CreateRepository());
        var result = await Orchestrator(client).ExecuteRoundAsync(
            trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        Assert.Equal(reservationId, result.ReservationId);
        Assert.Single(await attempts.LoadAttemptsAsync(trip, 1));
        var driver = (await _drivers.CreateRepository().LoadAsync(d1))!;
        Assert.Equal(DriverStatus.Reserved, driver.Status);
        Assert.Equal(reservationId, driver.ReservationId);
        Assert.Equal(1L, await DriverReservedOutboxAsync(d1));
        Assert.Equal(0L, await MatchingFailedAsync(trip));
    }

    // ------------------------------------------------------------------
    // L2: real HTTP boundary with controlled transport faults.
    // ------------------------------------------------------------------

    private sealed class FaultHandler : DelegatingHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _requests = [];

        public Func<HttpRequestMessage, HttpResponseMessage?>? Inject { get; set; }
        public bool ReplaceReserveResponse { get; set; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int Count(Func<string, bool> predicate)
        {
            lock (_gate)
            {
                return _requests.Count(predicate);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _requests.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
            }

            var injected = Inject?.Invoke(request);
            if (injected is not null) return injected;

            var response = await base.SendAsync(request, cancellationToken);
            if (ReplaceReserveResponse && request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/reserve", StringComparison.Ordinal))
            {
                Assert.True(response.IsSuccessStatusCode);
                response.Dispose();
                return Bare(HttpStatusCode.NotFound);
            }

            return response;
        }
    }

    private static HttpResponseMessage Bare(HttpStatusCode status) =>
        new(status) { Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain") };

    private static (HttpClient Http, FaultHandler Handler) CreateClient(string baseUrl)
    {
        var handler = new FaultHandler { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        return (http, handler);
    }

    private static WebApplication BuildDriverHost(DriverTestDatabase database, DriverFaultPoint faults)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var outbox = new NpgsqlOutboxStore(database.DataSource);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton(new DriverRepository(database.DataSource, outbox));
        builder.Services.AddSingleton(faults);
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>();
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();
        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single()
            .TrimEnd('/');

    [Fact]
    public async Task InFlight_Committed_Bare404OnVersionRead_ReconcilesWithoutReplayOrD2()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.InFlight, commit: true, lifecycle: "STARTED");
        var faults = DriverFaultPoint.Disabled();
        await using var app = BuildDriverHost(_drivers, faults);
        await app.StartAsync();
        var (http, handler) = CreateClient(AddressOf(app));
        using var _ = http;

        // A routing/proxy 404 for the version read and the reserve POST must
        // carry no authority: only the reservation query may decide.
        handler.Inject = request =>
            request.RequestUri!.AbsolutePath.StartsWith("/drivers/", StringComparison.Ordinal)
                && !request.RequestUri.AbsolutePath.Contains("/reservations/", StringComparison.Ordinal)
                ? Bare(HttpStatusCode.NotFound)
                : null;

        var client = new HttpDriverClient(http);
        var result = await Orchestrator(client).ReconcileAsync(
            scenario.Trip, scenario.Round, scenario.Ordinal);

        Assert.Equal(AttemptOutcome.Reserved, result.Outcome);
        Assert.Equal(scenario.ReservationId, result.ReservationId);
        Assert.Equal(0, handler.Count(r => r.StartsWith("POST ", StringComparison.Ordinal)
            && r.Contains("/reserve", StringComparison.Ordinal)));
        var row = Assert.Single(await Attempts().LoadAttemptsAsync(scenario.Trip, scenario.Round));
        Assert.Equal(MatchingAttemptStates.Reserved, row.State);
        Assert.Equal(0L, await MatchingFailedAsync(scenario.Trip));
        Assert.Equal(DriverStatus.OnTrip,
            (await _drivers.CreateRepository().LoadAsync(scenario.D1))!.Status);
        Assert.Null((await _drivers.CreateRepository().LoadAsync(scenario.D2))!.ReservationId);
        await app.StopAsync();
    }

    [Fact]
    public async Task FreshAttempt_Bare404OnVersionRead_IsNotConclusive_AndNeverAdvances()
    {
        var (d1, _) = await SeedAvailableAsync();
        var (d2, _) = await SeedAvailableAsync();
        var faults = DriverFaultPoint.Disabled();
        await using var app = BuildDriverHost(_drivers, faults);
        await app.StartAsync();
        var (http, handler) = CreateClient(AddressOf(app));
        using var _ = http;
        handler.Inject = request =>
            request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.StartsWith("/drivers/", StringComparison.Ordinal)
                ? Bare(HttpStatusCode.NotFound)
                : null;

        var trip = Guid.NewGuid();
        var result = await Orchestrator(new HttpDriverClient(http)).ExecuteRoundAsync(
            trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.BlockedOnUnknown, result.Outcome);
        var attempt = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(d1, attempt.DriverId);
        Assert.NotEqual(MatchingAttemptStates.Rejected, attempt.State);
        Assert.True(attempt.State is MatchingAttemptStates.InFlight or MatchingAttemptStates.Unknown);
        Assert.Equal(0L, await MatchingFailedAsync(trip));
        Assert.Equal(DriverStatus.Available, (await _drivers.CreateRepository().LoadAsync(d1))!.Status);
        Assert.Equal(DriverStatus.Available, (await _drivers.CreateRepository().LoadAsync(d2))!.Status);
        Assert.Null((await _drivers.CreateRepository().LoadAsync(d2))!.ReservationId);
        await app.StopAsync();
    }

    [Fact]
    public async Task FreshAttempt_Bare404OnReserve_IsNotConclusive_AndNeverAdvances()
    {
        var (d1, _) = await SeedAvailableAsync();
        var (d2, _) = await SeedAvailableAsync();
        var faults = DriverFaultPoint.Disabled();
        await using var app = BuildDriverHost(_drivers, faults);
        await app.StartAsync();
        var (http, handler) = CreateClient(AddressOf(app));
        using var _ = http;
        handler.Inject = request =>
            request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/reserve", StringComparison.Ordinal)
                ? Bare(HttpStatusCode.NotFound)
                : null;

        var trip = Guid.NewGuid();
        var result = await Orchestrator(new HttpDriverClient(http)).ExecuteRoundAsync(
            trip, 1, [d1, d2], Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.BlockedOnUnknown, result.Outcome);
        var attempts = await Attempts().LoadAttemptsAsync(trip, 1);
        var first = Assert.Single(attempts);
        Assert.Equal(d1, first.DriverId);
        Assert.True(first.State is MatchingAttemptStates.Unknown or MatchingAttemptStates.InFlight);
        Assert.Equal(0L, await MatchingFailedAsync(trip));
        Assert.Equal(DriverStatus.Available, (await _drivers.CreateRepository().LoadAsync(d1))!.Status);
        Assert.Equal(DriverStatus.Available, (await _drivers.CreateRepository().LoadAsync(d2))!.Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task CommittedReserve_ResponseReplacedWithBare404_BlocksD2AndRecoversSameIdentity()
    {
        var (d1, _) = await SeedAvailableAsync();
        var (d2, _) = await SeedAvailableAsync();
        await using var app = BuildDriverHost(_drivers, DriverFaultPoint.Disabled());
        await app.StartAsync();
        var (http, handler) = CreateClient(AddressOf(app));
        using var clientLifetime = http;
        handler.ReplaceReserveResponse = true;
        var trip = Guid.NewGuid();
        var orchestrator = Orchestrator(new HttpDriverClient(http));

        var first = await orchestrator.ExecuteRoundAsync(trip, 1, [d1, d2], trip.ToString("D"));
        Assert.Equal(RoundOutcome.BlockedOnUnknown, first.Outcome);
        var unresolved = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, unresolved.State);
        Assert.Equal(unresolved.ReservationId,
            (await _drivers.CreateRepository().LoadAsync(d1))!.ReservationId);
        Assert.Equal(MatchingRoundStatuses.Processing,
            (await Attempts().LoadRoundAsync(trip, 1))!.Status);
        Assert.Equal(DriverStatus.Available,
            (await _drivers.CreateRepository().LoadAsync(d2))!.Status);

        var recovered = await orchestrator.ExecuteRoundAsync(trip, 1, [d1, d2], trip.ToString("D"));
        Assert.Equal(RoundOutcome.Reserved, recovered.Outcome);
        Assert.Equal(d1, recovered.DriverId);
        Assert.Equal(unresolved.ReservationId, recovered.ReservationId);
        Assert.Equal(1, handler.Count(r => r.StartsWith("POST ", StringComparison.Ordinal)));
        Assert.Equal(MatchingAttemptStates.Reserved,
            Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1)).State);
        Assert.Equal(MatchingRoundStatuses.Reserved,
            (await Attempts().LoadRoundAsync(trip, 1))!.Status);
        Assert.Null((await _drivers.CreateRepository().LoadAsync(d2))!.ReservationId);
        Assert.Equal(1L, await DriverReservedOutboxAsync(d1));
        Assert.Equal(0L, await DriverReservedOutboxAsync(d2));
        Assert.Equal(0L, await MatchingFailedAsync(trip));
        await app.StopAsync();
    }

    [Fact]
    public async Task FreshAttempt_AuthoritativeMissingDriver_RemainsConclusive()
    {
        // Positive control: a real Driver-service 404 with the structured
        // domain body is authoritative and must still advance to the next
        // candidate (the fix must not block every legitimate rejection).
        var (d2, _) = await SeedAvailableAsync();
        var missing = Guid.NewGuid();
        var faults = DriverFaultPoint.Disabled();
        await using var app = BuildDriverHost(_drivers, faults);
        await app.StartAsync();
        var (http, _) = CreateClient(AddressOf(app));
        using var __ = http;

        var trip = Guid.NewGuid();
        var result = await Orchestrator(new HttpDriverClient(http)).ExecuteRoundAsync(
            trip, 1, [missing, d2], Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        Assert.Equal(d2, result.DriverId);
        var attempts = await Attempts().LoadAttemptsAsync(trip, 1);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(MatchingAttemptStates.Rejected, attempts[0].State);
        Assert.Equal("DRIVER_NOT_FOUND", attempts[0].LastErrorCode);
        Assert.Equal(MatchingAttemptStates.Reserved, attempts[1].State);
        Assert.Equal(DriverStatus.Reserved,
            (await _drivers.CreateRepository().LoadAsync(d2))!.Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task UnavailableReservationQuery_KeepsUnresolved_AndNeverFabricates()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.InFlight, commit: true, lifecycle: "STARTED");
        var faults = DriverFaultPoint.Disabled();
        await using var app = BuildDriverHost(_drivers, faults);
        await app.StartAsync();
        var (http, handler) = CreateClient(AddressOf(app));
        using var _ = http;
        handler.Inject = request =>
            request.RequestUri!.AbsolutePath.Contains("/reservations/", StringComparison.Ordinal)
                ? Bare(HttpStatusCode.ServiceUnavailable)
                : null;

        var orchestrator = Orchestrator(new HttpDriverClient(http));
        var result = await orchestrator.ReconcileAsync(
            scenario.Trip, scenario.Round, scenario.Ordinal);

        Assert.True(
            result.Outcome is AttemptOutcome.NeedsReview or AttemptOutcome.Unknown,
            $"unavailable query must stay unresolved, got {result.Outcome}");
        var row = Assert.Single(await Attempts().LoadAttemptsAsync(scenario.Trip, scenario.Round));
        Assert.Equal(MatchingAttemptStates.Unknown, row.State);
        Assert.Equal("RECONCILE_EXHAUSTED", row.LastErrorCode);
        var round = (await Attempts().LoadRoundAsync(scenario.Trip, scenario.Round))!;
        Assert.Equal(MatchingRoundStatuses.Processing, round.Status);
        Assert.Null(round.SelectedDriverId);
        Assert.Null(round.SelectedReservationId);
        Assert.Equal(0L, await MatchingFailedAsync(scenario.Trip));
        Assert.Equal(DriverStatus.OnTrip,
            (await _drivers.CreateRepository().LoadAsync(scenario.D1))!.Status);
        Assert.Equal(DriverStatus.Available,
            (await _drivers.CreateRepository().LoadAsync(scenario.D2))!.Status);
        await app.StopAsync();
    }

    [Fact]
    public async Task ConcurrentRecovery_ConvergesOnOneLogicalReservation()
    {
        var scenario = await SeedUnresolvedAttemptAsync(
            MatchingAttemptStates.Unknown, commit: true, lifecycle: "STARTED");
        var client = new RepositoryAuthorityClient(_drivers.CreateRepository());
        var orchestrator = Orchestrator(client);

        using var barrier = new Barrier(2);
        var runs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            return await orchestrator.ReconcileAsync(
                scenario.Trip, scenario.Round, scenario.Ordinal);
        })));

        Assert.All(runs, r =>
        {
            Assert.Equal(AttemptOutcome.Reserved, r.Outcome);
            Assert.Equal(scenario.ReservationId, r.ReservationId);
        });
        Assert.Equal(1L, await DriverReservedOutboxAsync(scenario.D1));
        var row = Assert.Single(await Attempts().LoadAttemptsAsync(scenario.Trip, scenario.Round));
        Assert.Equal(MatchingAttemptStates.Reserved, row.State);
        var round = (await Attempts().LoadRoundAsync(scenario.Trip, scenario.Round))!;
        Assert.Equal(MatchingRoundStatuses.Reserved, round.Status);
        Assert.Equal(scenario.ReservationId, round.SelectedReservationId);
        Assert.Equal(0L, await MatchingFailedAsync(scenario.Trip));
    }
}
