// Shared helpers for B-REC-01 production-path proofs. Every test in this
// group uses the REAL Matching process, REAL PostgreSQL/Redis/RabbitMQ and
// REAL Driver Kestrel endpoints. Oracles are authoritative rows and remote
// effects, never handler return codes alone.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Topology;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

public abstract class ProductionMatchingTestBase
{
    protected const string FaultAfterReserveCommit =
        RideHailing.DriverService.Infrastructure.Faults.DriverFaultPointNames.AfterReserveCommitBeforeResponse;

    protected ProductionMatchingTestBase(
        MatchingPostgresDatabase pg,
        DriverTestDatabase drivers,
        RedisDatabase redis,
        RabbitMqDatabase rabbit,
        ITestOutputHelper output)
    {
        Pg = pg;
        Drivers = drivers;
        Redis = redis;
        Rabbit = rabbit;
        Output = output;
    }

    protected MatchingPostgresDatabase Pg { get; }

    protected DriverTestDatabase Drivers { get; }

    protected RedisDatabase Redis { get; }

    protected RabbitMqDatabase Rabbit { get; }

    protected ITestOutputHelper Output { get; }

    protected sealed record RoundState(
        Guid TripId,
        int Round,
        string Status,
        string Stage,
        string? StageEvaluated,
        bool CancelRequested,
        Guid? SelectedDriverId,
        Guid? SelectedReservationId,
        Guid? ResultMessageId,
        string InputMessageId,
        string? WorkErrorCode,
        double? PickupLongitude,
        double? PickupLatitude,
        bool AutoExecute,
        string? SnapshotJson);

    protected sealed record AttemptState(
        int Ordinal,
        Guid DriverId,
        Guid ReservationId,
        string State,
        string Stage,
        string? LastErrorCode);

    protected sealed record DriverState(
        string Status,
        int Version,
        Guid? ReservationId,
        Guid? ReservedTripId);

    private static int _pickupSeed;

    /// <summary>
    /// Isolated GEOSEARCH center per test. Shared Redis persists across the
    /// collection; regions are spaced far enough apart that another test's
    /// projections can never enter these radii.
    /// </summary>
    protected static (double Latitude, double Longitude) NewPickup()
    {
        var index = Interlocked.Increment(ref _pickupSeed) - 1;
        var latitude = 10.0 + (index % 30) * 2.5;
        var longitude = -170.0 + (index / 30) * 5.0;
        return (latitude, longitude);
    }

    protected sealed record StageRadii(double S0, double S1, double S2)
    {
        public static StageRadii Default { get; } = new(1.0, 3.0, 5.0);
    }

    protected ConnectionFactory RabbitFactory() => new()
    {
        Uri = new Uri(Rabbit.ConnectionString),
        ClientProvidedName = "b-rec01-production-proof",
        AutomaticRecoveryEnabled = false,
    };

    /// <summary>
    /// Test isolation only: a failed/abandoned scenario must not leave a
    /// PROCESSING auto-execute round that a later scenario's worker would
    /// drive against the wrong Driver host. Production never abandons work.
    /// </summary>
    protected async Task AbandonPriorTestWorkAsync()
    {
        await using var conn = new NpgsqlConnection(Pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE matching_rounds
            SET auto_execute = FALSE,
                cancel_requested = TRUE,
                work_error_code = COALESCE(work_error_code, 'TEST_HARNESS_ABANDONED'),
                updated_at = now()
            WHERE status = 'PROCESSING' AND auto_execute = TRUE;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task PurgeBrokerAsync()
    {
        using var connection = await RabbitFactory().CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in new[]
                 {
                     "matching.ride-requested.q",
                     "matching.commands.q",
                     "matching.driver-projection.q",
                     "trip.matching-outcome.q",
                 })
        {
            await channel.QueuePurgeAsync(queue);
        }
    }

    protected async Task<byte[]> PublishRideRequestedAsync(
        Guid tripId, int round, string messageId,
        double latitude = 10.8, double longitude = 106.6)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            tripId,
            matchingRound = round,
            pickup = new { latitude, longitude },
            dropoff = new { latitude = latitude + 0.05, longitude = longitude + 0.05 },
            requestedAt = DateTimeOffset.UtcNow,
        });
        var envelope = MessageEnvelope<JsonElement>.Create(
            "RideRequestedV1", Guid.NewGuid().ToString("N"), payload, messageId: messageId);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        await PublishAsync("ride.events", "trip.ride-requested.v1", body);
        return body;
    }

    protected async Task PublishCancelAsync(Guid tripId, int round)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            tripId,
            matchingRound = round,
        });
        var envelope = MessageEnvelope<JsonElement>.Create(
            "CancelMatchingRoundV1",
            Guid.NewGuid().ToString("N"),
            payload,
            messageId: $"cancel-{tripId:N}-{round}");
        await PublishAsync(
            "ride.commands", "matching.cancel-round.v1",
            JsonSerializer.SerializeToUtf8Bytes(envelope));
    }

    protected async Task PublishAsync(string exchange, string routingKey, byte[] body)
    {
        using var connection = await RabbitFactory().CreateConnectionAsync();
        await RideTopology.DeclareAsync(connection);
        await using var channel = await connection.CreateChannelAsync();
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
        };
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, props, body);
    }

    protected async Task<Driver> SeedAvailableDriverAsync(
        double latitude, double longitude)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await Drivers.CreateRepository().TryInsertAsync(driver));
        await SeedProjectionAsync(driver.DriverId, driver.Version, latitude, longitude);
        return driver;
    }

    protected async Task SeedProjectionOnlyAsync(
        Guid driverId, double latitude, double longitude, int version = 1)
    {
        await SeedProjectionAsync(driverId, version, latitude, longitude);
    }

    private async Task SeedProjectionAsync(
        Guid driverId, int version, double latitude, double longitude)
    {
        var updater = new RedisProjectionUpdater(Redis.Multiplexer());
        await updater.EnsureFunctionAsync();
        await updater.ApplyStateAsync(new DriverStateProjectionUpdate(
            driverId, "AVAILABLE", version));
        await updater.ApplyLocationAsync(new LocationProjectionUpdate(
            driverId, longitude, latitude, $"prod-{driverId:N}", 1,
            DateTimeOffset.UtcNow));
    }

    protected async Task<MatchingServiceProcess> StartMatchingAsync(
        string driverBaseUrl,
        string? redisConnection = null,
        StageRadii? radii = null,
        int driverTimeoutSeconds = 5,
        TimeSpan? reconcileBackoff = null,
        CancellationToken ct = default)
    {
        return await MatchingServiceProcess.StartAsync(
            MatchingEnvironment(
                driverBaseUrl, redisConnection, radii, driverTimeoutSeconds, reconcileBackoff),
            ct);
    }

    protected Dictionary<string, string?> MatchingEnvironment(
        string driverBaseUrl,
        string? redisConnection = null,
        StageRadii? radii = null,
        int driverTimeoutSeconds = 5,
        TimeSpan? reconcileBackoff = null)
    {
        var rabbit = new Uri(Rabbit.ConnectionString);
        var credentials = rabbit.UserInfo.Split(':', 2);
        var stages = radii ?? StageRadii.Default;
        var timeout = TimeSpan.FromSeconds(driverTimeoutSeconds);
        var backoff = reconcileBackoff ?? TimeSpan.FromMilliseconds(100);

        return new Dictionary<string, string?>
        {
            ["ConnectionStrings__Postgres"] = Pg.ConnectionString,
            ["Redis__ConnectionString"] = redisConnection ?? Redis.ConnectionString,
            ["RabbitMq__Host"] = rabbit.Host,
            ["RabbitMq__Port"] = rabbit.Port.ToString(CultureInfo.InvariantCulture),
            ["RabbitMq__Username"] = credentials.Length > 0 ? Uri.UnescapeDataString(credentials[0]) : "guest",
            ["RabbitMq__Password"] = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "guest",
            ["RabbitMq__ConsumersEnabled"] = "true",
            ["RabbitMq__PublishingEnabled"] = "true",
            ["Services__DriverBaseUrl"] = driverBaseUrl,
            ["DriverClient__TimeoutSeconds"] = driverTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["Matching__Worker__Enabled"] = "true",
            ["Matching__Worker__PollInterval"] = "00:00:00.100",
            ["Matching__Worker__ReconcileBackoff"] = backoff.ToString("c", CultureInfo.InvariantCulture),
            ["Matching__Worker__BatchSize"] = "32",
            ["Matching__Worker__MaxConcurrentRounds"] = "8",
            ["Matching__SearchRadiusKm"] = stages.S0.ToString("R", CultureInfo.InvariantCulture),
            ["Matching__MaxCandidates"] = "20",
            ["Matching__DiscoveryOversample"] = "3",
            ["Matching__DiscoveryHardCap"] = "100",
            ["Matching__LocationFreshnessWindow"] = "00:05:00",
            ["Matching__RankingConfigVersion"] = "v1",
            ["Matching__Stages__0__Name"] = "S0",
            ["Matching__Stages__0__SearchRadiusKm"] = stages.S0.ToString("R", CultureInfo.InvariantCulture),
            ["Matching__Stages__0__MaxCandidates"] = "20",
            ["Matching__Stages__1__Name"] = "S1",
            ["Matching__Stages__1__SearchRadiusKm"] = stages.S1.ToString("R", CultureInfo.InvariantCulture),
            ["Matching__Stages__1__MaxCandidates"] = "20",
            ["Matching__Stages__2__Name"] = "S2",
            ["Matching__Stages__2__SearchRadiusKm"] = stages.S2.ToString("R", CultureInfo.InvariantCulture),
            ["Matching__Stages__2__MaxCandidates"] = "20",
            ["Logging__LogLevel__Default"] = "Warning",
            ["Logging__LogLevel__Microsoft"] = "Warning",
        };
    }

    protected async Task<RoundState?> LoadRoundAsync(Guid tripId, int round)
    {
        await using var conn = new NpgsqlConnection(Pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT trip_id, matching_round, status, current_stage, stage_evaluated,
                   cancel_requested, selected_driver_id, selected_reservation_id,
                   result_message_id, input_message_id, work_error_code,
                   pickup_longitude, pickup_latitude, auto_execute,
                   candidate_snapshot_json::text
            FROM matching_rounds
            WHERE trip_id = @trip AND matching_round = @round;
            """, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RoundState(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetDouble(11),
            reader.IsDBNull(12) ? null : reader.GetDouble(12),
            reader.GetBoolean(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    protected async Task<IReadOnlyList<AttemptState>> LoadAttemptsAsync(Guid tripId, int round)
    {
        await using var conn = new NpgsqlConnection(Pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT ordinal, driver_id, reservation_id, state, stage, last_error_code
            FROM matching_attempts
            WHERE trip_id = @trip AND matching_round = @round
            ORDER BY ordinal;
            """, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<AttemptState>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AttemptState(
                reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    protected async Task<DriverState?> LoadDriverAsync(Guid driverId)
    {
        await using var conn = new NpgsqlConnection(Drivers.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT status, version, reservation_id, reserved_trip_id
            FROM drivers WHERE driver_id = @id;
            """, conn);
        cmd.Parameters.AddWithValue("id", driverId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new DriverState(
            reader.GetString(0), reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    protected async Task<long> DriverReservedOutboxCountAsync(Guid driverId)
    {
        await using var conn = new NpgsqlConnection(Drivers.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM outbox_messages
            WHERE message_type = 'DriverReservedV1' AND aggregate_id = @id;
            """, conn);
        cmd.Parameters.AddWithValue("id", driverId.ToString("N"));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    protected async Task<long> DriverReservationIdentityCountAsync(Guid driverId)
    {
        await using var conn = new NpgsqlConnection(Drivers.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(DISTINCT reservation_id) FROM drivers WHERE driver_id = @id;", conn);
        cmd.Parameters.AddWithValue("id", driverId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    protected async Task<long> MatchingFailedCountAsync(Guid tripId)
    {
        await using var conn = new NpgsqlConnection(Pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM outbox_messages
            WHERE message_type = 'MatchingFailedV1'
              AND payload_json->>'tripId' = @trip;
            """, conn);
        cmd.Parameters.AddWithValue("trip", tripId.ToString("D"));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    protected async Task<long> QueueDepthAsync(string queue)
    {
        using var connection = await RabbitFactory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var info = await channel.QueueDeclarePassiveAsync(queue);
        return info.MessageCount;
    }

    protected async Task<bool> CancelIntentExistsAsync(Guid tripId, int round)
    {
        await using var conn = new NpgsqlConnection(Pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM matching_cancel_intents
                WHERE trip_id = @trip AND matching_round = @round);
            """, conn);
        cmd.Parameters.AddWithValue("trip", tripId);
        cmd.Parameters.AddWithValue("round", round);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    protected static async Task PollUntilAsync(
        Func<Task<bool>> condition, string what, int timeoutSeconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting for {what}. Last error: {last?.Message ?? "none"}.");
    }
}
