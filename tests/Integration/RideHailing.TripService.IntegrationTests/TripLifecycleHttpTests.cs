// GET /trips/{id} + POST /trips/{id}/cancel HTTP adapter proofs against the
// real application host and real PostgreSQL 18.x: route registration,
// Rider identity/role gating, rider-scoped ownership (missing and not-owned
// share 404 without disclosure), response/status mapping, diagnostic
// correlation echo, and proof that rejected auth/identity requests cause no
// business side effects. Lifecycle states are driven through the W5
// application handlers against the same disposable database the SUT reads.

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
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripLifecycleHttpTests : IDisposable
{
    private readonly TripPostgresFixture _fixture;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TripLifecycleHttpTests(TripPostgresFixture fixture)
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

    private HttpRequestMessage Request(
        HttpMethod method,
        string url,
        string? riderId = null,
        string? role = null,
        HttpContent? body = null,
        string? idempotencyKey = null,
        string? correlationId = null)
    {
        var message = new HttpRequestMessage(method, url) { Content = body };
        if (riderId is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, riderId);
        }

        if (role is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, role);
        }

        if (idempotencyKey is not null)
        {
            message.Headers.Add(TripIdentityHeaders.IdempotencyKey, idempotencyKey);
        }

        if (correlationId is not null)
        {
            message.Headers.Add(TripIdentityHeaders.CorrelationId, correlationId);
        }

        return message;
    }

    private static StringContent RideBody() =>
        new(JsonSerializer.Serialize(new
        {
            pickup = new { latitude = 10.762622, longitude = 106.660172 },
            dropoff = new { latitude = 10.776889, longitude = 106.700806 },
        }), Encoding.UTF8, "application/json");

    private async Task<Guid> CreateRideAsync(string riderId, string key)
    {
        using var response = await _client.SendAsync(
            Request(HttpMethod.Post, TripRoutes.CreateRide, riderId, "Rider", RideBody(), key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(body);
        return body.TripId;
    }

    private async Task StartMatchingAsync(Guid tripId, Guid riderId)
    {
        await using var db = new TripDbContext(PlainOptions());
        var result = await new StartMatchingHandler(db, new FixedTimeProvider(ActionAt), NullLogger<StartMatchingHandler>.Instance)
            .HandleAsync(tripId, riderId);
        Assert.True(result.IsSuccess);
    }

    private async Task ReserveAsync(Guid tripId, Guid driverId, Guid reservationId)
    {
        await using var db = new TripDbContext(PlainOptions());
        var result = await new RecordDriverReservationHandler(db, new FixedTimeProvider(ActionAt), NullLogger<RecordDriverReservationHandler>.Instance)
            .HandleAsync(tripId, driverId, reservationId, 1);
        Assert.True(result.IsSuccess);
    }

    private async Task<Trip> ReloadAsync(Guid tripId)
    {
        await using var db = new TripDbContext(PlainOptions());
        return await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code)
            ? code.GetString() ?? string.Empty
            : string.Empty;
    }

    [Fact]
    public async Task GetTrip_ReturnsAuthoritativeTruth_ThroughLifecycle()
    {
        var rider = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(rider, "http-get-" + Guid.NewGuid().ToString("N"));

        using (var created = await _client.SendAsync(
            Request(HttpMethod.Get, $"/trips/{tripId:D}", rider, "Rider")))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var body = await created.Content.ReadFromJsonAsync<GetTripResponse>();
            Assert.NotNull(body);
            Assert.Equal(tripId, body.TripId);
            Assert.Equal("CREATED", body.Status);
            Assert.Equal(0, body.MatchingRound);
            Assert.Null(body.AssignedDriverId);
            Assert.Null(body.ReservationId);
        }

        await StartMatchingAsync(tripId, Guid.Parse(rider));
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await ReserveAsync(tripId, driverId, reservationId);

        using (var reserved = await _client.SendAsync(
            Request(HttpMethod.Get, $"/trips/{tripId:D}", rider, "Rider")))
        {
            Assert.Equal(HttpStatusCode.OK, reserved.StatusCode);
            var body = await reserved.Content.ReadFromJsonAsync<GetTripResponse>();
            Assert.NotNull(body);
            Assert.Equal("DRIVER_RESERVED", body.Status);
            Assert.Equal(1, body.MatchingRound);
            Assert.Equal(driverId, body.AssignedDriverId);
            Assert.Equal(reservationId, body.ReservationId);
        }
    }

    [Fact]
    public async Task GetTrip_WithoutPrincipal_Returns401_WithoutSideEffects()
    {
        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/trips/{Guid.NewGuid():D}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetTrip_WithoutRiderRole_Returns403()
    {
        var rider = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(rider, "http-get-role-" + Guid.NewGuid().ToString("N"));

        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/trips/{tripId:D}", rider, "Driver"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetTrip_OtherRidersTrip_Returns404_WithoutDisclosureOrMutation()
    {
        var owner = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(owner, "http-get-owned-" + Guid.NewGuid().ToString("N"));
        await StartMatchingAsync(tripId, Guid.Parse(owner));
        var before = await ReloadAsync(tripId);

        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/trips/{tripId:D}", Guid.NewGuid().ToString("D"), "Rider"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));

        var after = await ReloadAsync(tripId);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.CurrentMatchingRound, after.CurrentMatchingRound);
    }

    [Fact]
    public async Task GetTrip_EchoesSuppliedCorrelationId()
    {
        var rider = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(rider, "http-get-corr-" + Guid.NewGuid().ToString("N"));

        using var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"/trips/{tripId:D}", rider, "Rider", correlationId: "ride-flow-test-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(TripIdentityHeaders.CorrelationId, out var values));
        Assert.Equal("ride-flow-test-1", Assert.Single(values));
    }

    [Fact]
    public async Task CancelRide_FromMatching_Transitions_AndSecondCancelReplaysTerminalState()
    {
        var rider = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(rider, "http-cancel-" + Guid.NewGuid().ToString("N"));
        await StartMatchingAsync(tripId, Guid.Parse(rider));

        using (var cancel = await _client.SendAsync(
            Request(HttpMethod.Post, $"/trips/{tripId:D}/cancel", rider, "Rider")))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var body = await cancel.Content.ReadFromJsonAsync<CancelRideResponse>();
            Assert.NotNull(body);
            Assert.Equal(tripId, body.TripId);
            Assert.Equal("CANCELLED", body.Status);
        }

        var persisted = await ReloadAsync(tripId);
        Assert.Equal(TripState.Cancelled, persisted.State);

        using (var again = await _client.SendAsync(
            Request(HttpMethod.Post, $"/trips/{tripId:D}/cancel", rider, "Rider")))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var body = await again.Content.ReadFromJsonAsync<CancelRideResponse>();
            Assert.Equal("CANCELLED", body!.Status);
        }
        var replayed = await ReloadAsync(tripId);
        Assert.Equal(persisted.Version, replayed.Version);
        Assert.Equal(persisted.UpdatedAt, replayed.UpdatedAt);
        Assert.Single(replayed.CleanupIntents);
    }

    [Fact]
    public async Task CancelRide_FromCreated_ReturnsPolicyUnresolved_WithoutMutation()
    {
        var rider = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(rider, "http-cancel-created-" + Guid.NewGuid().ToString("N"));

        using var response = await _client.SendAsync(
            Request(HttpMethod.Post, $"/trips/{tripId:D}/cancel", rider, "Rider"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CANCELLATION_POLICY_UNRESOLVED", await ProblemCodeAsync(response));

        var persisted = await ReloadAsync(tripId);
        Assert.Equal(TripState.Created, persisted.State);
        Assert.Equal(0, persisted.Version);
    }

    [Fact]
    public async Task CancelRide_OtherRidersTrip_Returns404_WithoutSideEffects()
    {
        var owner = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(owner, "http-cancel-owned-" + Guid.NewGuid().ToString("N"));
        await StartMatchingAsync(tripId, Guid.Parse(owner));

        using var response = await _client.SendAsync(
            Request(HttpMethod.Post, $"/trips/{tripId:D}/cancel", Guid.NewGuid().ToString("D"), "Rider"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var persisted = await ReloadAsync(tripId);
        Assert.Equal(TripState.Matching, persisted.State);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task CancelRide_WithoutPrincipal_Returns401_WithoutSideEffects()
    {
        var rider = Guid.NewGuid().ToString("D");
        var tripId = await CreateRideAsync(rider, "http-cancel-anon-" + Guid.NewGuid().ToString("N"));
        await StartMatchingAsync(tripId, Guid.Parse(rider));

        using var response = await _client.SendAsync(
            Request(HttpMethod.Post, $"/trips/{tripId:D}/cancel"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var persisted = await ReloadAsync(tripId);
        Assert.Equal(TripState.Matching, persisted.State);
        Assert.Equal(1, persisted.Version);
    }
}
