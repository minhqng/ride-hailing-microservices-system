// AUD-02 public Driver lifecycle transport proofs against the real Trip host
// and real PostgreSQL. The only test override is the Driver directory (the
// production client calls the Driver service); identity, role, assignment and
// reservation fencing all run through the production endpoint and aggregate.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.TripService.Api;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.DriverDirectory;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripDriverLifecycleHttpTests : IDisposable
{
    private readonly TripPostgresFixture _fixture;
    private readonly FakeDriverDirectory _directory = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TripDriverLifecycleHttpTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new TripApplicationFactory(fixture.ConnectionString)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddTransient<IStartupFilter, TestPrincipalStartupFilter>();
                services.AddSingleton<IDriverDirectory>(_directory);
            }));
        _client = _factory.CreateClient();

        using var setup = new TripDbContext(PlainOptions());
        setup.Database.Migrate();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private DbContextOptions<TripDbContext> PlainOptions() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeDriverDirectory : IDriverDirectory
    {
        private readonly Dictionary<Guid, Guid> _owners = new();

        public void Register(Guid driverId, Guid userId) => _owners[driverId] = userId;

        public Task<DriverLookup> LookupAsync(
            Guid driverId, string? authorization, CancellationToken cancellationToken) =>
            Task.FromResult(_owners.TryGetValue(driverId, out var userId)
                ? new DriverLookup(DriverLookupStatus.Found, userId)
                : new DriverLookup(DriverLookupStatus.NotFound));
    }

    private sealed record ReservedTrip(Guid TripId, Guid RiderId, Guid DriverId, Guid UserId, Guid ReservationId);

    private static readonly DateTimeOffset ActionAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> CreateRideAsync(Guid rider, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TripRoutes.CreateRide)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                pickup = new { latitude = 10.762622, longitude = 106.660172 },
                dropoff = new { latitude = 10.776889, longitude = 106.700806 },
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString("D"));
        request.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
        request.Headers.Add(TripIdentityHeaders.IdempotencyKey, key);

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(body);
        return body.TripId;
    }

    private async Task<ReservedTrip> ReservedTripAsync()
    {
        var rider = Guid.NewGuid();
        var tripId = await CreateRideAsync(rider, "driver-api-" + Guid.NewGuid().ToString("N"));

        await using (var db = new TripDbContext(PlainOptions()))
        {
            Assert.True((await new StartMatchingHandler(db, new FixedTimeProvider(ActionAt),
                    NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider)).IsSuccess);
        }

        var driverId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        _directory.Register(driverId, userId);

        await using (var db = new TripDbContext(PlainOptions()))
        {
            Assert.True((await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt),
                    NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        return new ReservedTrip(tripId, rider, driverId, userId, reservationId);
    }

    private async Task AuthorizeAsync(Guid tripId)
    {
        await using var db = new TripDbContext(PlainOptions());
        Assert.True((await new MarkPaymentAuthorizedHandler(db, new FixedTimeProvider(ActionAt),
                NullLogger<MarkPaymentAuthorizedHandler>.Instance)
            .HandleAsync(tripId)).IsSuccess);
    }

    private HttpRequestMessage DriverRequest(
        Guid tripId, string action, Guid userId, Guid driverId, Guid reservationId,
        string role = "Driver")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId:D}/{action}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { driverId, reservationId }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, userId.ToString("D"));
        request.Headers.Add(TestPrincipalStartupFilter.RoleHeader, role);
        return request;
    }

    private async Task<Trip> ReloadAsync(Guid tripId)
    {
        await using var db = new TripDbContext(PlainOptions());
        return await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
    }

    private async Task<int> IntentCountAsync(Guid tripId, string messageType)
    {
        await using var db = new TripDbContext(PlainOptions());
        return await db.OutboxMessages.CountAsync(
            message => message.AggregateId == tripId.ToString("D") &&
                       message.MessageType == messageType);
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString() ?? string.Empty
            : string.Empty;
    }

    [Fact]
    public async Task Accept_ByAssignedDriver_AdvancesAndStagesAuthorization()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DriverLifecycleResponse>();
        Assert.NotNull(body);
        Assert.Equal("DRIVER_ACCEPTED", body.State);
        Assert.False(body.Replayed);

        var persisted = await ReloadAsync(trip.TripId);
        Assert.Equal(TripState.DriverAccepted, persisted.State);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "AuthorizePaymentV1"));
    }

    [Fact]
    public async Task Accept_Duplicate_ReplaysWithoutAnotherIntent()
    {
        var trip = await ReservedTripAsync();
        using (var first = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var accepted = await ReloadAsync(trip.TripId);
        using var replay = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var body = await replay.Content.ReadFromJsonAsync<DriverLifecycleResponse>();
        Assert.True(body!.Replayed);
        var afterReplay = await ReloadAsync(trip.TripId);
        Assert.Equal(accepted.Version, afterReplay.Version);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "AuthorizePaymentV1"));
    }

    [Fact]
    public async Task Accept_WithSomeoneElsesProfile_Returns403()
    {
        var trip = await ReservedTripAsync();
        var foreignDriver = Guid.NewGuid();
        _directory.Register(foreignDriver, Guid.NewGuid());

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, foreignDriver, trip.ReservationId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Accept_ByWrongDriverForTheAssignment_Returns409()
    {
        var trip = await ReservedTripAsync();
        var otherDriver = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        _directory.Register(otherDriver, otherUser);

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", otherUser, otherDriver, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ASSIGNMENT_MISMATCH", await ProblemCodeAsync(response));
        Assert.Equal(TripState.DriverReserved, (await ReloadAsync(trip.TripId)).State);
    }

    [Fact]
    public async Task Accept_WithWrongReservation_Returns409()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ASSIGNMENT_MISMATCH", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Accept_ByRider_Returns403()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId, "Rider"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Accept_WithoutPrincipal_Returns401()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/trips/{trip.TripId:D}/accept",
            new { driverId = trip.DriverId, reservationId = trip.ReservationId });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Accept_UnknownDriverProfile_Returns404()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, Guid.NewGuid(), trip.ReservationId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("DRIVER_NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Start_FromReadyToStart_AdvancesAndStagesDriverCommand()
    {
        var trip = await ReservedTripAsync();
        using (var accepted = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await AuthorizeAsync(trip.TripId);

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "start", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DriverLifecycleResponse>();
        Assert.Equal("IN_PROGRESS", body!.State);
        Assert.Equal(TripState.InProgress, (await ReloadAsync(trip.TripId)).State);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "MarkDriverTripStartedV1"));
    }

    [Fact]
    public async Task Complete_FromInProgress_AdvancesAndStagesCapture()
    {
        var trip = await ReservedTripAsync();
        using (var accepted = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await AuthorizeAsync(trip.TripId);
        using (var started = await _client.SendAsync(
            DriverRequest(trip.TripId, "start", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "complete", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DriverLifecycleResponse>();
        Assert.Equal("COMPLETED", body!.State);
        Assert.Equal(TripState.Completed, (await ReloadAsync(trip.TripId)).State);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "CapturePaymentV1"));
    }

    [Fact]
    public async Task CompletedTrip_CannotAcceptOrStartAgain()
    {
        var trip = await ReservedTripAsync();
        using (var accepted = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await AuthorizeAsync(trip.TripId);
        using (var started = await _client.SendAsync(
            DriverRequest(trip.TripId, "start", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        using (var completed = await _client.SendAsync(
            DriverRequest(trip.TripId, "complete", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        foreach (var action in new[] { "accept", "start", "complete" })
        {
            using var response = await _client.SendAsync(
                DriverRequest(trip.TripId, action, trip.UserId, trip.DriverId, trip.ReservationId));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("TERMINAL_STATE", await ProblemCodeAsync(response));
        }
    }

    [Fact]
    public async Task Accept_MissingReservationField_Returns400()
    {
        var trip = await ReservedTripAsync();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/trips/{trip.TripId:D}/accept")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { driverId = trip.DriverId }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, trip.UserId.ToString("D"));
        request.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Driver");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reject_ByAssignedDriver_TerminalizesWithExactCleanupAndNoPayment()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DriverRejectResponse>();
        Assert.NotNull(body);
        Assert.Equal("MATCH_FAILED", body.State);
        Assert.Equal("DRIVER_REJECTED", body.FailureReason);
        Assert.False(body.Replayed);
        Assert.True(body.CleanupPending);

        var persisted = await ReloadAsync(trip.TripId);
        Assert.Equal(TripState.MatchFailed, persisted.State);
        Assert.Equal(TripFailureReason.DriverRejected, persisted.FailureReason);
        Assert.Null(persisted.Assignment);
        Assert.Equal(1, persisted.CurrentMatchingRound);
        var release = Assert.Single(persisted.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(trip.DriverId, release.DriverId);
        Assert.Equal(trip.ReservationId, release.ReservationId);
        Assert.Null(release.CompletedAt);
        Assert.Single(persisted.CleanupIntents, i => i.Kind == TripCleanupKind.CancelMatching);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "ReleaseDriverReservationV1"));
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "CancelMatchingRoundV1"));
        Assert.Equal(0, await IntentCountAsync(trip.TripId, "AuthorizePaymentV1"));
        Assert.Equal(0, await IntentCountAsync(trip.TripId, "CapturePaymentV1"));
        Assert.Equal(0, await IntentCountAsync(trip.TripId, "VoidPaymentAuthorizationV1"));

        // Duplicate reject is a replay: no second obligation, no version change.
        using var replay = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, trip.ReservationId));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<DriverRejectResponse>();
        Assert.True(replayBody!.Replayed);
        Assert.Equal("DRIVER_REJECTED", replayBody.FailureReason);
        var afterReplay = await ReloadAsync(trip.TripId);
        Assert.Equal(persisted.Version, afterReplay.Version);
        Assert.Equal(2, afterReplay.CleanupIntents.Count);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "ReleaseDriverReservationV1"));

        // A rejection never starts the payment saga.
        await using (var db = new TripDbContext(PlainOptions()))
        {
            Assert.False(await db.RideSagas.AnyAsync(s => s.TripId == trip.TripId));
        }

        // Authority confirmation clears the visible cleanup flag only when
        // every exact obligation is discharged.
        await using (var db = new TripDbContext(PlainOptions()))
        {
            var cleanup = new ConfirmTripCleanupHandler(db, new FixedTimeProvider(ActionAt));
            Assert.True((await cleanup.HandleAsync(trip.TripId, release.Key,
                CleanupAuthorityResult.Released)).IsSuccess);
            var matching = Assert.Single(afterReplay.CleanupIntents, i => i.Kind == TripCleanupKind.CancelMatching);
            Assert.True((await cleanup.HandleAsync(trip.TripId, matching.Key,
                CleanupAuthorityResult.MatchingStopped)).IsSuccess);
        }

        using var confirmedReplay = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, trip.ReservationId));
        var confirmedBody = await confirmedReplay.Content.ReadFromJsonAsync<DriverRejectResponse>();
        Assert.True(confirmedBody!.Replayed);
        Assert.False(confirmedBody.CleanupPending);

        // The Rider's one-active-Trip slot is free: a new ride is legal.
        var newTrip = await CreateRideAsync(trip.RiderId, "after-reject-" + Guid.NewGuid().ToString("N"));
        Assert.NotEqual(trip.TripId, newTrip);
    }

    [Fact]
    public async Task Reject_ByForeignDriver_Returns403()
    {
        var trip = await ReservedTripAsync();
        var foreignDriver = Guid.NewGuid();
        _directory.Register(foreignDriver, Guid.NewGuid());

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, foreignDriver, trip.ReservationId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
        Assert.Equal(TripState.DriverReserved, (await ReloadAsync(trip.TripId)).State);
    }

    [Fact]
    public async Task Reject_ByRider_Returns403()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, trip.ReservationId, "Rider"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
        Assert.Equal(TripState.DriverReserved, (await ReloadAsync(trip.TripId)).State);
    }

    [Fact]
    public async Task Reject_WithoutPrincipal_Returns401()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/trips/{trip.TripId:D}/reject",
            new { driverId = trip.DriverId, reservationId = trip.ReservationId });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
        Assert.Equal(TripState.DriverReserved, (await ReloadAsync(trip.TripId)).State);
    }

    [Fact]
    public async Task Reject_WithWrongReservation_Returns409WithoutCleanup()
    {
        var trip = await ReservedTripAsync();

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ASSIGNMENT_MISMATCH", await ProblemCodeAsync(response));
        var persisted = await ReloadAsync(trip.TripId);
        Assert.Equal(TripState.DriverReserved, persisted.State);
        Assert.Empty(persisted.CleanupIntents);
        Assert.Equal(0, await IntentCountAsync(trip.TripId, "ReleaseDriverReservationV1"));
    }

    [Fact]
    public async Task Reject_ByAnotherAssignedDriver_Returns409WithoutCleanup()
    {
        var trip = await ReservedTripAsync();
        var otherDriver = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        _directory.Register(otherDriver, otherUser);

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", otherUser, otherDriver, trip.ReservationId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ASSIGNMENT_MISMATCH", await ProblemCodeAsync(response));
        Assert.Equal(TripState.DriverReserved, (await ReloadAsync(trip.TripId)).State);
    }

    [Fact]
    public async Task Reject_AfterAccept_Returns409AndStagesNoRelease()
    {
        var trip = await ReservedTripAsync();
        using (var accepted = await _client.SendAsync(
            DriverRequest(trip.TripId, "accept", trip.UserId, trip.DriverId, trip.ReservationId)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("DOMAIN_CONFLICT", await ProblemCodeAsync(response));
        var persisted = await ReloadAsync(trip.TripId);
        Assert.Equal(TripState.DriverAccepted, persisted.State);
        Assert.Empty(persisted.CleanupIntents);
        Assert.Equal(0, await IntentCountAsync(trip.TripId, "ReleaseDriverReservationV1"));
    }

    [Fact]
    public async Task Reject_AfterRiderCancel_Returns409WithoutSecondObligation()
    {
        var trip = await ReservedTripAsync();

        await using (var db = new TripDbContext(PlainOptions()))
        {
            Assert.True((await new CancelRideHandler(db, new FixedTimeProvider(ActionAt),
                NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(trip.TripId, trip.RiderId)).IsSuccess);
        }

        using var response = await _client.SendAsync(
            DriverRequest(trip.TripId, "reject", trip.UserId, trip.DriverId, trip.ReservationId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TERMINAL_STATE", await ProblemCodeAsync(response));
        var persisted = await ReloadAsync(trip.TripId);
        Assert.Equal(TripState.Cancelled, persisted.State);
        Assert.Single(persisted.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(1, await IntentCountAsync(trip.TripId, "ReleaseDriverReservationV1"));
    }
}
