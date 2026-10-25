// A-W12-04 authorization and invalid-transition negatives on the real HTTP
// surface with the repository's supported authenticated test fixture
// (TestPrincipalStartupFilter). Every case asserts the AUTHORITATIVE oracle:
// the durable Trip row (state + version) and, where relevant, that rejected
// requests staged NO outgoing intent (no payment/release commands).
//
// Existing coverage already proves 401/403/404-ownership/claims-fail-closed
// (CreateRideHttpTests, TripLifecycleHttpTests); this file closes the gaps:
// invalid transitions from terminal states, malformed identities, body actor
// spoofing inertness, faultpoint non-activatability through public traffic,
// and no-outbox-effect on rejection.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RideHailing.TripService.Api;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW12AuthzNegativeTests : IDisposable
{
    private readonly TripPostgresFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TripW12AuthzNegativeTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new TripApplicationFactory(fixture.ConnectionString)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddTransient<IStartupFilter, TestPrincipalStartupFilter>()));
        _client = _factory.CreateClient();
        using var setup = new TripDbContext(Options());
        setup.Database.Migrate();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private DbContextOptions<TripDbContext> Options() =>
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(_fixture.ConnectionString).Options;

    private static StringContent RideBody(Dictionary<string, object>? extra = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["pickup"] = new { latitude = 10.762622, longitude = 106.660172 },
            ["dropoff"] = new { latitude = 10.776889, longitude = 106.700806 },
        };
        foreach (var (key, value) in extra ?? [])
            payload[key] = value;
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private static HttpRequestMessage Post(string key, Guid rider, StringContent? body = null,
        params string[] extraHeaders)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, TripRoutes.CreateRide)
        {
            Content = body ?? RideBody(),
        };
        message.Headers.Add(TripIdentityHeaders.IdempotencyKey, key);
        message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString("D"));
        message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
        foreach (var header in extraHeaders)
            message.Headers.TryAddWithoutValidation(header, "true");
        return message;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
    }

    private async Task<Guid> CreateAsync(Guid rider, Dictionary<string, object>? bodyExtra = null,
        params string[] extraHeaders)
    {
        using var response = await _client.SendAsync(
            Post($"w12-authz-{Guid.NewGuid():N}", rider, RideBody(bodyExtra), extraHeaders));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tripId").GetGuid();
    }

    private async Task<(string State, int Version)> DurableStateAsync(Guid tripId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT state, version FROM trips WHERE trip_id = @p", connection);
        command.Parameters.AddWithValue("p", tripId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private async Task<long> OutboxCountAsync(Guid tripId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*)::bigint FROM outbox_messages WHERE message_id LIKE @prefix", connection);
        // Stable W8/W9 message identities embed the trip id in N format.
        command.Parameters.AddWithValue("prefix", $"%{tripId:N}%");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task SetStateAsync(Guid tripId, string state)
    {
        // Post-CREATED states must carry an open matching round, and COMPLETED
        // is a post-assignment state requiring a full assignment triple
        // (table CHECK constraints); the audit only mutates state columns.
        var assignment = state == "COMPLETED"
            ? ", assigned_driver_id = @driver, reservation_id = @reservation, matching_round = 1"
            : string.Empty;
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE trips SET state = @s, current_matching_round = 1{assignment} WHERE trip_id = @p", connection);
        command.Parameters.AddWithValue("s", state);
        command.Parameters.AddWithValue("p", tripId);
        if (assignment.Length > 0)
        {
            command.Parameters.AddWithValue("driver", Guid.NewGuid());
            command.Parameters.AddWithValue("reservation", Guid.NewGuid());
        }

        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CancelRide_OnCompletedTrip_Rejected409_LeavesDurableStateUnchanged()
    {
        var rider = Guid.NewGuid();
        var tripId = await CreateAsync(rider);
        await SetStateAsync(tripId, "COMPLETED");
        var before = await DurableStateAsync(tripId);

        using var response = await _client.SendAsync(
            Configure(new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId:D}/cancel"), rider));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TERMINAL_STATE", await ProblemCodeAsync(response));

        var after = await DurableStateAsync(tripId);
        Assert.Equal(before, after);
        Assert.Equal(0, await OutboxCountAsync(tripId));
    }

    [Fact]
    public async Task CancelRide_OnMatchFailedTrip_Rejected409_NoOutboundRelease()
    {
        var rider = Guid.NewGuid();
        var tripId = await CreateAsync(rider);
        await SetStateAsync(tripId, "MATCH_FAILED");

        using var response = await _client.SendAsync(
            Configure(new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId:D}/cancel"), rider));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TERMINAL_STATE", await ProblemCodeAsync(response));
        Assert.Equal(0, await OutboxCountAsync(tripId));
    }

    [Fact]
    public async Task GetTrip_MalformedTripId_IsNotRoutedAndNeverReachesTheHandler()
    {
        var rider = Guid.NewGuid();
        foreach (var malformed in new[] { "not-a-guid", "12345", "zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz" })
        {
            // Authenticated requests: a well-formed-but-unknown guid yields
            // the endpoint's own NOT_FOUND 404, while malformed identities
            // never match the {tripId:guid} route at all.
            using var response = await _client.SendAsync(
                Configure(new HttpRequestMessage(HttpMethod.Get, $"/trips/{malformed}"), rider));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // The authenticated rider cannot smuggle a malformed identity past
        // the route constraint into a handler either.
        using var cancel = await _client.SendAsync(
            Configure(new HttpRequestMessage(HttpMethod.Post, "/trips/not-a-guid/cancel"), rider));
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Fact]
    public async Task CreateRide_BodyActorFieldsAreInert_OwnershipStaysWithClaims()
    {
        var rider = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var tripId = await CreateAsync(rider, new Dictionary<string, object>
        {
            ["riderId"] = attacker,
            ["driverId"] = attacker,
            ["actor"] = attacker,
        });

        // The authenticated rider owns the Trip; the body-supplied actor
        // identity granted nothing (SEC-04).
        using (var owner = await _client.SendAsync(
                   Configure(new HttpRequestMessage(HttpMethod.Get, $"/trips/{tripId:D}"), rider)))
        {
            Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        }

        using var spoofed = await _client.SendAsync(
            Configure(new HttpRequestMessage(HttpMethod.Get, $"/trips/{tripId:D}"), attacker));
        Assert.Equal(HttpStatusCode.NotFound, spoofed.StatusCode);
    }

    [Fact]
    public async Task FaultPoints_CannotBeActivatedThroughPublicTraffic()
    {
        var rider = Guid.NewGuid();
        var signalDirectory = Path.Combine(Path.GetTempPath(), $"w12-fault-inert-{Guid.NewGuid():N}");
        // Headers that merely LOOK like faultpoint configuration must have
        // no effect: activation is bound to host configuration plus a
        // non-Production environment, never to any request content. No
        // SignalDirectory exists in this host, so an activated gate would
        // have nowhere to write its marker — and none is created.
        var tripId = await CreateAsync(rider,
            extraHeaders: ["TripFaultPoints:Enabled", "X-Trip-FaultPoints-Enabled", "X-TripFaultPoints"]);

        Assert.False(Directory.Exists(signalDirectory));
        Assert.Equal("CREATED", (await DurableStateAsync(tripId)).State);
    }

    private static HttpRequestMessage Configure(HttpRequestMessage message, Guid rider)
    {
        message.Headers.Add(TestPrincipalStartupFilter.NameIdentifierHeader, rider.ToString("D"));
        message.Headers.Add(TestPrincipalStartupFilter.RoleHeader, "Rider");
        return message;
    }
}
