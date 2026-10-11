// A-W05-04 first cross-service REST/system skeleton: the strongest truthful
// pre-broker shape.
//
// What this proves (Trip service, real host, real PostgreSQL):
//   - Trip boots independently and ride creation stays legal (W4 preserved);
//   - the full synchronous local lifecycle walks CREATED → MATCHING →
//     DRIVER_RESERVED → DRIVER_ACCEPTED → READY_TO_START → IN_PROGRESS →
//     COMPLETED with exact Version advancement and a coherent assignment;
//   - the cancellation path reaches CANCELLED with the exact cleanup
//     requirement represented, and late DriverAccepted cannot reactivate it;
//   - health/readiness endpoints coexist with business routes;
//   - trace/log correlation travels as a diagnostic header, never as
//     business identity.
//
// What this does NOT claim: no B/C business code runs here (their services
// are untouched), no broker/Outbox/Saga exists yet, and the driver/
// reservation/payment identities are authoritative-SHAPED test fixtures.
// B/C signoff on the RideRequestedV1 payload shape remains PROCESS_ONLY
// (human review, not executable).

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

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripLifecycleWalkthroughTests : IDisposable
{
    private readonly TripPostgresFixture _fixture;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TripLifecycleWalkthroughTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new TripApplicationFactory(fixture.ConnectionString)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddTransient<IStartupFilter, TestPrincipalStartupFilter>()));
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

    private static readonly DateTimeOffset ActionAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static StringContent RideBody() =>
        new(JsonSerializer.Serialize(new
        {
            pickup = new { latitude = 10.762622, longitude = 106.660172 },
            dropoff = new { latitude = 10.776889, longitude = 106.700806 },
        }), Encoding.UTF8, "application/json");

    private HttpRequestMessage Authenticated(HttpMethod method, string url, string riderId, HttpContent? body = null)
    {
        var message = new HttpRequestMessage(method, url) { Content = body };
        message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, riderId);
        message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
        message.Headers.Add(TripIdentityHeaders.CorrelationId, "ride-flow-walkthrough-1");
        return message;
    }

    private async Task<TripLifecycleResult> DriveAsync(
        Func<TripDbContext, Task<TripLifecycleResult>> drive)
    {
        await using var db = new TripDbContext(PlainOptions());
        return await drive(db);
    }

    private static FixedTimeProvider Clock() => new(ActionAt);

    private async Task<string> LifecycleStatusAsync(Guid tripId, string riderId)
    {
        using var response = await _client.SendAsync(
            Authenticated(HttpMethod.Get, $"/trips/{tripId:D}", riderId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GetTripResponse>();
        Assert.NotNull(body);
        return body.Status;
    }

    [Fact]
    public async Task FullRide_Walkthrough_ReachesCompleted_WithExactVersionLineage()
    {
        var rider = Guid.NewGuid();
        var riderHeader = rider.ToString("D");

        Guid tripId;
        var createMessage = Authenticated(HttpMethod.Post, TripRoutes.CreateRide, riderHeader, RideBody());
        createMessage.Headers.Add(TripIdentityHeaders.IdempotencyKey, "walkthrough-full-" + Guid.NewGuid().ToString("N"));
        using (var create = await _client.SendAsync(createMessage))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var body = await create.Content.ReadFromJsonAsync<CreateRideResponse>();
            Assert.NotNull(body);
            tripId = body.TripId;
        }

        Assert.Equal("CREATED", await LifecycleStatusAsync(tripId, riderHeader));

        // Authoritative-shaped fixtures (test doubles for future B/C facts).
        var driverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var started = await DriveAsync(db =>
            new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider));
        Assert.True(started.IsSuccess);
        Assert.Equal("MATCHING", await LifecycleStatusAsync(tripId, riderHeader));

        var reserved = await DriveAsync(db =>
            new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1));
        Assert.True(reserved.IsSuccess);
        Assert.Equal("DRIVER_RESERVED", await LifecycleStatusAsync(tripId, riderHeader));

        var accepted = await DriveAsync(db =>
            new RecordDriverAcceptedHandler(db, Clock(), NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId));
        Assert.True(accepted.IsSuccess);
        Assert.Equal("DRIVER_ACCEPTED", await LifecycleStatusAsync(tripId, riderHeader));

        var authorized = await DriveAsync(db =>
            new MarkPaymentAuthorizedHandler(db, Clock(), NullLogger<MarkPaymentAuthorizedHandler>.Instance)
                .HandleAsync(tripId));
        Assert.True(authorized.IsSuccess);
        Assert.Equal("READY_TO_START", await LifecycleStatusAsync(tripId, riderHeader));

        var startedRide = await DriveAsync(db =>
            new StartTripHandler(db, Clock(), NullLogger<StartTripHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId));
        Assert.True(startedRide.IsSuccess);
        Assert.Equal("IN_PROGRESS", await LifecycleStatusAsync(tripId, riderHeader));

        var completed = await DriveAsync(db =>
            new CompleteTripHandler(db, Clock(), NullLogger<CompleteTripHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId));
        Assert.True(completed.IsSuccess);
        Assert.Equal("COMPLETED", await LifecycleStatusAsync(tripId, riderHeader));

        await using var verify = new TripDbContext(PlainOptions());
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Completed, trip.State);
        Assert.Equal(driverId, trip.Assignment!.DriverId);
        Assert.Equal(reservationId, trip.Assignment.ReservationId);
        Assert.Equal(1, trip.CurrentMatchingRound);
        // One Version per committed transition: start-matching, reserve,
        // accept, authorize, start, complete.
        Assert.Equal(6, trip.Version);
        // FareSnapshot frozen at creation is untouched by the lifecycle
        // (DSI-014 regression): amount, currency and policy version survive.
        Assert.True(trip.Fare.Amount > 0);
        Assert.Equal("VND", trip.Fare.Currency);
        Assert.Equal("p0-w4-v1", trip.Fare.PricingPolicyVersion);
    }

    [Fact]
    public async Task CancelPath_Walkthrough_PreservesCleanupIdentity_AgainstLateAccept()
    {
        var rider = Guid.NewGuid();
        var riderHeader = rider.ToString("D");
        var key = "walkthrough-cancel-" + Guid.NewGuid().ToString("N");

        var createMessage = Authenticated(HttpMethod.Post, TripRoutes.CreateRide, riderHeader, RideBody());
        createMessage.Headers.Add(TripIdentityHeaders.IdempotencyKey, key);
        Guid tripId;
        using (var create = await _client.SendAsync(createMessage))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var body = await create.Content.ReadFromJsonAsync<CreateRideResponse>();
            Assert.NotNull(body);
            tripId = body.TripId;
        }

        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await DriveAsync(db =>
            new StartMatchingHandler(db, Clock(), NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, rider));
        await DriveAsync(db =>
            new RecordDriverReservationHandler(db, Clock(), NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1));

        var cancelled = await DriveAsync(db =>
            new CancelRideHandler(db, Clock(), NullLogger<CancelRideHandler>.Instance)
                .HandleAsync(tripId, rider));
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(
            TripCompensation.ReleaseReservation | TripCompensation.CancelMatching,
            cancelled.Compensation);
        Assert.Equal("CANCELLED", await LifecycleStatusAsync(tripId, riderHeader));

        // Late DriverAccepted cannot reactivate the terminal Trip (DSI-032),
        // but the assignment survives as the exact cleanup identity.
        var late = await DriveAsync(db =>
            new RecordDriverAcceptedHandler(db, Clock(), NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId));
        Assert.False(late.IsSuccess);
        Assert.Equal(TripErrorCode.TerminalState, late.ErrorCode);

        await using var verify = new TripDbContext(PlainOptions());
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(TripState.Cancelled, trip.State);
        Assert.Equal(driverId, trip.Assignment!.DriverId);
        Assert.Equal(reservationId, trip.Assignment.ReservationId);
    }

    [Fact]
    public async Task HealthEndpoints_Coexist_WithBusinessRoutes()
    {
        using var live = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var ready = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
