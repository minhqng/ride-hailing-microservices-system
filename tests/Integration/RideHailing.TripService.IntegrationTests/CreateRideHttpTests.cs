// POST /trips HTTP adapter proofs against the real application host and real
// PostgreSQL 18.x: route registration, Rider identity extraction from the
// authenticated principal, Idempotency-Key handling, transport mapping,
// outcome/status mapping, and rider-spoofing resistance. Authentication
// itself is NOT implemented here: the test host injects principals via the
// test-only TestPrincipalStartupFilter; production has no such mechanism.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RideHailing.TripService.Api;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class CreateRideHttpTests : IDisposable
{
    private readonly TripPostgresFixture _fixture;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CreateRideHttpTests(TripPostgresFixture fixture)
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

    private static StringContent RideBody(double pickupLat = 10.762622, double pickupLon = 106.660172, double dropLat = 10.776889, double dropLon = 106.700806) =>
        new(JsonSerializer.Serialize(new
        {
            pickup = new { latitude = pickupLat, longitude = pickupLon },
            dropoff = new { latitude = dropLat, longitude = dropLon },
        }), Encoding.UTF8, "application/json");

    private HttpRequestMessage Post(
        string? key = null,
        string? nameId = null,
        string? sub = null,
        string? role = null,
        HttpContent? body = null,
        bool duplicateKey = false,
        string? secondKey = null,
        IEnumerable<string>? nameIdValues = null,
        IEnumerable<string>? subValues = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, TripRoutes.CreateRide)
        {
            Content = body ?? RideBody(),
        };
        if (key is not null)
        {
            message.Headers.Add(TripIdentityHeaders.IdempotencyKey, key);
        }

        if (duplicateKey)
        {
            message.Headers.Add(TripIdentityHeaders.IdempotencyKey, secondKey ?? key ?? "k2");
        }

        if (nameIdValues is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, nameIdValues);
        }
        else if (nameId is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, nameId);
        }

        if (subValues is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.SubjectHeader, subValues);
        }
        else if (sub is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.SubjectHeader, sub);
        }

        if (role is not null)
        {
            message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, role);
        }

        return message;
    }

    private static string Rider() => Guid.NewGuid().ToString("D");

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code)
            ? code.GetString() ?? string.Empty
            : string.Empty;
    }

    [Fact]
    public async Task AnonymousRequest_Returns401()
    {
        using var response = await _client.SendAsync(Post(key: $"anon-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedNonRider_Returns403()
    {
        using var response = await _client.SendAsync(
            Post(key: $"driver-{Guid.NewGuid():N}", nameId: Rider(), role: "Driver"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("NameIdentifier")]
    [InlineData("Sub")]
    [InlineData("Both")]
    public async Task ClaimCompatibility_Matrix_AcceptsKnownRepresentations(string mode)
    {
        var rider = Rider();
        var nameId = mode == "Sub" ? null : rider;
        var sub = mode == "NameIdentifier" ? null : rider;

        using var response = await _client.SendAsync(
            Post(key: $"claim-{mode}-{Guid.NewGuid():N}", nameId: nameId, sub: sub, role: "Rider"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.TripId);
    }

    [Theory]
    [InlineData("Conflict")]
    [InlineData("Missing")]
    public async Task ClaimCompatibility_Matrix_RejectsBadIdentity(string mode)
    {
        var riderA = Rider();
        var riderB = Rider();
        HttpRequestMessage message = mode switch
        {
            "Conflict" => Post(key: $"claim-bad-{Guid.NewGuid():N}", nameId: riderA, sub: riderB, role: "Rider"),
            _ => Post(key: $"claim-bad-{Guid.NewGuid():N}", role: "Rider"),
        };

        using var response = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedGuidClaim_Returns401()
    {
        using var response = await _client.SendAsync(
            Post(key: $"claim-malformed-{Guid.NewGuid():N}", nameId: "not-a-guid", role: "Rider"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_Then_Replay_Maps201Then200_WithSameTripId()
    {
        var rider = Rider();
        var key = $"http-replay-{Guid.NewGuid():N}";

        using var created = await _client.SendAsync(Post(key: key, nameId: rider, role: "Rider"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(createdBody);
        Assert.NotEqual(Guid.Empty, createdBody.TripId);
        Assert.Equal($"/trips/{createdBody.TripId:D}", created.Headers.Location?.ToString());

        using var replayed = await _client.SendAsync(Post(key: key, nameId: rider, role: "Rider"));
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replayedBody = await replayed.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(replayedBody);
        Assert.Equal(createdBody.TripId, replayedBody.TripId);

        await using var verify = new TripDbContext(PlainOptions());
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == createdBody.TripId);
        Assert.Equal(Guid.Parse(rider), trip.RiderId);
        Assert.Equal(TripState.Created, trip.State);
        var scope = CreateRideRequestFingerprint.BuildScope(Guid.Parse(rider));
        var record = await verify.RequestIdempotency.AsNoTracking()
            .SingleAsync(r => r.Scope == scope && r.Key == key);
        Assert.Equal(createdBody.TripId, record.ResourceId);
        Assert.Equal(1, await verify.Trips.AsNoTracking().CountAsync(t => t.RiderId == Guid.Parse(rider)));
    }

    [Fact]
    public async Task SameKey_DifferentRequest_Maps409IdempotencyConflict()
    {
        var rider = Rider();
        var key = $"http-conflict-{Guid.NewGuid():N}";

        using var created = await _client.SendAsync(Post(key: key, nameId: rider, role: "Rider"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var otherBody = new StringContent(
            JsonSerializer.Serialize(new
            {
                pickup = new { latitude = 10.762622, longitude = 106.660172 },
                dropoff = new { latitude = 11.0, longitude = 107.0 },
            }), Encoding.UTF8, "application/json");
        using var conflict = await _client.SendAsync(
            Post(key: key, nameId: rider, role: "Rider", body: otherBody));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ProblemCodeAsync(conflict));
    }

    [Fact]
    public async Task FreshKey_WhileActiveTripExists_Maps409DomainConflict()
    {
        var rider = Rider();

        using var created = await _client.SendAsync(
            Post(key: $"http-active-{Guid.NewGuid():N}", nameId: rider, role: "Rider"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var conflict = await _client.SendAsync(
            Post(key: $"http-active2-{Guid.NewGuid():N}", nameId: rider, role: "Rider"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("DOMAIN_CONFLICT", await ProblemCodeAsync(conflict));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("oversized")]
    public async Task KeyValidation_Matrix_Returns400(string mode)
    {
        string? actualKey = mode switch
        {
            "missing" => null,
            "oversized" => new string('k', 129),
            "blank" => "   ",
            _ => null,
        };
        HttpRequestMessage message = mode == "duplicate"
            ? Post(key: $"dup-a-{Guid.NewGuid():N}", nameId: Rider(), role: "Rider", duplicateKey: true, secondKey: $"dup-b-{Guid.NewGuid():N}")
            : Post(key: actualKey, nameId: Rider(), role: "Rider");

        using var response = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task InvalidCoordinates_Returns400()
    {
        using var response = await _client.SendAsync(Post(
            key: $"http-coords-{Guid.NewGuid():N}",
            nameId: Rider(),
            role: "Rider",
            body: RideBody(pickupLat: 91.0)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task BodyCannotOverride_RiderIdentity()
    {
        var rider = Rider();
        var spoofed = Guid.NewGuid();
        var key = $"http-spoof-{Guid.NewGuid():N}";
        var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                pickup = new { latitude = 10.762622, longitude = 106.660172 },
                dropoff = new { latitude = 10.776889, longitude = 106.700806 },
                riderId = spoofed,
            }), Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(
            Post(key: key, nameId: rider, role: "Rider", body: body));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(created);

        await using var verify = new TripDbContext(PlainOptions());
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == created.TripId);
        Assert.Equal(Guid.Parse(rider), trip.RiderId);
        Assert.NotEqual(spoofed, trip.RiderId);
    }

    [Fact]
    public async Task DuplicateIdenticalNameIdentifier_Accepted_And_PersistsRiderId()
    {
        var rider = Guid.NewGuid();
        var riderText = rider.ToString("D");
        var key = $"http-dup-nameid-same-{Guid.NewGuid():N}";

        using var response = await _client.SendAsync(Post(
            key: key,
            role: "Rider",
            nameIdValues: new[] { riderText, riderText }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.TripId);

        await using var verify = new TripDbContext(PlainOptions());
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == body.TripId);
        Assert.Equal(rider, trip.RiderId);
        Assert.Equal(TripState.Created, trip.State);
    }

    [Fact]
    public async Task ConflictingNameIdentifier_Rejects401_WithoutSideEffect()
    {
        var riderA = Guid.NewGuid();
        var riderB = Guid.NewGuid();
        var key = $"http-dup-nameid-conflict-{Guid.NewGuid():N}";

        using var response = await _client.SendAsync(Post(
            key: key,
            role: "Rider",
            nameIdValues: new[] { riderA.ToString("D"), riderB.ToString("D") }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));

        await using var verify = new TripDbContext(PlainOptions());
        Assert.Equal(0, await verify.Trips.AsNoTracking()
            .CountAsync(t => t.RiderId == riderA || t.RiderId == riderB));
        Assert.Equal(0, await verify.RequestIdempotency.AsNoTracking()
            .CountAsync(r => r.Key == key));
    }

    [Fact]
    public async Task DuplicateIdenticalSub_Accepted_And_PersistsRiderId()
    {
        var rider = Guid.NewGuid();
        var riderText = rider.ToString("D");
        var key = $"http-dup-sub-same-{Guid.NewGuid():N}";

        using var response = await _client.SendAsync(Post(
            key: key,
            role: "Rider",
            subValues: new[] { riderText, riderText }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateRideResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.TripId);

        await using var verify = new TripDbContext(PlainOptions());
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == body.TripId);
        Assert.Equal(rider, trip.RiderId);
        Assert.Equal(TripState.Created, trip.State);
    }

    [Fact]
    public async Task ConflictingSub_Rejects401_WithoutSideEffect()
    {
        var riderA = Guid.NewGuid();
        var riderB = Guid.NewGuid();
        var key = $"http-dup-sub-conflict-{Guid.NewGuid():N}";

        using var response = await _client.SendAsync(Post(
            key: key,
            role: "Rider",
            subValues: new[] { riderA.ToString("D"), riderB.ToString("D") }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));

        await using var verify = new TripDbContext(PlainOptions());
        Assert.Equal(0, await verify.Trips.AsNoTracking()
            .CountAsync(t => t.RiderId == riderA || t.RiderId == riderB));
        Assert.Equal(0, await verify.RequestIdempotency.AsNoTracking()
            .CountAsync(r => r.Key == key));
    }

    [Fact]
    public async Task ValidNameIdentifier_Plus_MalformedSecondaryNameIdentifier_Rejects401()
    {
        var rider = Guid.NewGuid();
        var key = $"http-dup-nameid-malformed-{Guid.NewGuid():N}";

        using var response = await _client.SendAsync(Post(
            key: key,
            role: "Rider",
            nameIdValues: new[] { rider.ToString("D"), "not-a-guid" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));

        await using var verify = new TripDbContext(PlainOptions());
        Assert.Equal(0, await verify.Trips.AsNoTracking()
            .CountAsync(t => t.RiderId == rider));
        Assert.Equal(0, await verify.RequestIdempotency.AsNoTracking()
            .CountAsync(r => r.Key == key));
    }

    [Fact]
    public async Task ValidSub_Plus_MalformedSecondarySub_Rejects401()
    {
        var rider = Guid.NewGuid();
        var key = $"http-dup-sub-malformed-{Guid.NewGuid():N}";

        using var response = await _client.SendAsync(Post(
            key: key,
            role: "Rider",
            subValues: new[] { rider.ToString("D"), "not-a-guid" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));

        await using var verify = new TripDbContext(PlainOptions());
        Assert.Equal(0, await verify.Trips.AsNoTracking()
            .CountAsync(t => t.RiderId == rider));
        Assert.Equal(0, await verify.RequestIdempotency.AsNoTracking()
            .CountAsync(r => r.Key == key));
    }
}
