// Invariant-auditor execution.
//
// The auditor runs against REAL final states on real PostgreSQL, one
// disposable database per scenario (an auditor report describes ONE
// scenario's final state, not the cross-test residue of a shared DB):
//   1. a fully compensated scenario driven by the REAL handlers (create →
//      matching → reservation → accept → cancel → release confirm → DECLINED)
//      must PASS every Trip-executable check and explicitly record the
//      cross-service checks as NOT RUN (never silently green);
//   2. seedable violations (leaked reservation, open obligation after
//      quiescence, stuck outbox/saga rows, conflicting assignment
//      references, contradictory terminal receipts) must FAIL with findings
//      — proving the auditor is not vacuous;
//   3. mid-flight and quiesced observation modes behave differently.
//
// The Trip-side proxy is additionally STRUCTURALLY enforced by the
// ride_sagas CHECK constraints (lineage) + UNIQUE trip_id; the auditor still
// re-verifies it with independent read-only SQL — defense in depth, not a
// substitute.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripInvariantAuditorTests(TripPostgresFixture fixture)
{
    private DbContextOptions<TripDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(connectionString).Options;

    /// <summary>Creates, migrates and later drops one disposable audit database.</summary>
    private async Task<string> CreateAuditDatabaseAsync()
    {
        var name = $"trip_w12_aud_{Guid.NewGuid():N}";
        var maintenance = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Database = "postgres" }.ToString();
        await using (var connection = new NpgsqlConnection(maintenance))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Database = name }.ToString();
        await using (var db = new TripDbContext(Options(connectionString)))
        {
            await db.Database.MigrateAsync();
        }

        return connectionString;
    }

    private static async Task DropAsync(string connectionString)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ToString();
        await using var connection = new NpgsqlConnection(maintenance);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE \"{new NpgsqlConnectionStringBuilder(connectionString).Database}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    private static AuditStatus Status(AuditorReport report, string id) =>
        report.Checks.Single(c => c.Id == id).Status;

    [Fact]
    public async Task CompensatedScenario_AuditorPassesExecutableChecks_AndRecordsCrossServiceNotRun()
    {
        var connectionString = await CreateAuditDatabaseAsync();
        try
        {
            var tripId = await DriveCompensatedScenarioAsync(connectionString);

            var report = await TripInvariantAuditor.AuditAsync(connectionString,
                AuditorPolicy.QuiescedDefaults, runId: $"AUD-{Guid.NewGuid():N}",
                commit: TripInvariantAuditor.CommitPlaceholder);

            // Level A: Trip-authoritative rows inspected.
            Assert.Equal(AuditStatus.Pass, Status(report, "AUD-03"));
            Assert.Equal(AuditStatus.Pass, Status(report, "AUD-08"));

            // Level B: Trip-local supporting proofs carry the distinct
            // LOCAL_PROXY_PASS verdict — they must never read as a full
            // distributed-invariant PASS in the machine-readable report.
            Assert.Equal(AuditStatus.LocalProxyPass, Status(report, "AUD-05"));
            Assert.Equal(AuditStatus.LocalProxyPass, Status(report, "AUD-06"));
            Assert.Equal(AuditStatus.LocalProxyPass, Status(report, "AUD-07"));

            // Cross-service checks are dependencies, not silent green.
            Assert.Equal(AuditStatus.NotRun, Status(report, "AUD-01"));
            Assert.Equal(AuditStatus.NotRun, Status(report, "AUD-02"));
            Assert.Equal(AuditStatus.NotRun, Status(report, "AUD-04"));

            // The saga genuinely reached terminal COMPENSATED before the audit.
            await using var db = new TripDbContext(Options(connectionString));
            var saga = await db.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId);
            Assert.Equal(Domain.Sagas.RideSagaStatus.Compensated, saga.Status);
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    [Fact]
    public async Task SeededLeakedReservation_FailsAud07WithFinding()
    {
        var connectionString = await CreateAuditDatabaseAsync();
        try
        {
            await SeedTerminalAssignedTripAsync(connectionString, releaseIntent: false);

            var report = await TripInvariantAuditor.AuditAsync(connectionString,
                AuditorPolicy.QuiescedDefaults, "AUD-LEAK", TripInvariantAuditor.CommitPlaceholder);

            var check = report.Checks.Single(c => c.Id == "AUD-07");
            Assert.Equal(AuditStatus.Fail, check.Status);
            Assert.Contains(check.Findings, f => f.Detail.Contains("leaked"));
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    [Fact]
    public async Task OpenReleaseObligation_FailsWhenQuiesced_PassesMidFlight()
    {
        var connectionString = await CreateAuditDatabaseAsync();
        try
        {
            await SeedTerminalAssignedTripAsync(connectionString, releaseIntent: true);

            var quiesced = await TripInvariantAuditor.AuditAsync(connectionString,
                new AuditorPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), Quiesced: true),
                "AUD-OPEN-Q", TripInvariantAuditor.CommitPlaceholder);
            Assert.Equal(AuditStatus.Fail, Status(quiesced, "AUD-07"));

            var midFlight = await TripInvariantAuditor.AuditAsync(connectionString,
                new AuditorPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), Quiesced: false),
                "AUD-OPEN-M", TripInvariantAuditor.CommitPlaceholder);
            Assert.Equal(AuditStatus.LocalProxyPass, Status(midFlight, "AUD-07"));
            Assert.Contains("recognized-pending", midFlight.Checks.Single(c => c.Id == "AUD-07").Note);
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    [Fact]
    public async Task StuckOutboxAndSaga_FailAud08WithAges()
    {
        var connectionString = await CreateAuditDatabaseAsync();
        try
        {
            var tripId = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using (var stuck = new NpgsqlCommand("""
                INSERT INTO outbox_messages
                    (message_id, message_type, schema_version, correlation_id, payload_json, headers_json,
                     exchange, routing_key, occurred_at, next_attempt_at)
                VALUES (@message, 'RideRequestedV1', 1, 'aud',
                        @payload::jsonb, '{}'::jsonb,
                        'ride.events', 'trip.ride-requested.v1',
                        now() - interval '2 hours', now() - interval '2 hours')
                """, connection))
            {
                stuck.Parameters.AddWithValue("message", $"aud-stuck-{tripId:N}");
                stuck.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new { tripId }));
                await stuck.ExecuteNonQueryAsync();
            }

            await using (var saga = new NpgsqlCommand("""
                INSERT INTO ride_sagas
                    (saga_id, trip_id, status, current_step, step_version, reservation_id,
                     authorize_operation_key, created_at, updated_at)
                VALUES (@saga, @trip, 'REQUIRES_REVIEW', 'RELEASE_PENDING', 1, @reservation,
                        @key, now() - interval '2 hours', now() - interval '2 hours')
                """, connection))
            {
                saga.Parameters.AddWithValue("saga", Guid.NewGuid());
                saga.Parameters.AddWithValue("trip", tripId);
                saga.Parameters.AddWithValue("reservation", Guid.NewGuid());
                saga.Parameters.AddWithValue("key", $"{tripId:D}:authorize:v1");
                await saga.ExecuteNonQueryAsync();
            }

            var report = await TripInvariantAuditor.AuditAsync(connectionString,
                AuditorPolicy.QuiescedDefaults, "AUD-STUCK", TripInvariantAuditor.CommitPlaceholder);

            var check = report.Checks.Single(c => c.Id == "AUD-08");
            Assert.Equal(AuditStatus.Fail, check.Status);
            Assert.Contains(check.Findings, f => f.Detail.Contains("aud-stuck"));
            Assert.Contains(check.Findings, f => f.Detail.Contains("REQUIRES_REVIEW"));
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    [Fact]
    public async Task ConflictingAssignmentReferences_FailAud03()
    {
        var connectionString = await CreateAuditDatabaseAsync();
        try
        {
            // One trip, current assignment reservation A, plus an OPEN release
            // obligation pointing at a different reservation B: two active
            // assignment references.
            var tripId = Guid.NewGuid();
            var current = Guid.NewGuid();
            var stale = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO trips (trip_id, rider_id, state, version, created_at, current_matching_round, updated_at,
                                   pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude,
                                   fare_currency, fare_amount, fare_policy_version, fare_created_at,
                                   assigned_driver_id, reservation_id, matching_round)
                VALUES (@trip, @rider, 'DRIVER_RESERVED', 2, now(), 1, now(),
                        10.7, 106.6, 10.8, 106.7, 'VND', 50000, 'v1', now(),
                        @driver, @current, 1)
                """, connection))
            {
                seed.Parameters.AddWithValue("trip", tripId);
                seed.Parameters.AddWithValue("rider", Guid.NewGuid());
                seed.Parameters.AddWithValue("driver", Guid.NewGuid());
                seed.Parameters.AddWithValue("current", current);
                await seed.ExecuteNonQueryAsync();
            }

            await using (var release = new NpgsqlCommand("""
                INSERT INTO trip_cleanup_intents
                    (trip_id, key, kind, driver_id, reservation_id, matching_round, created_at)
                VALUES (@trip, @key, 'ReleaseReservation', @driver, @stale, 1, now())
                """, connection))
            {
                release.Parameters.AddWithValue("trip", tripId);
                release.Parameters.AddWithValue("key", $"release:{stale:D}");
                release.Parameters.AddWithValue("driver", Guid.NewGuid());
                release.Parameters.AddWithValue("stale", stale);
                await release.ExecuteNonQueryAsync();
            }

            var report = await TripInvariantAuditor.AuditAsync(connectionString,
                AuditorPolicy.QuiescedDefaults, "AUD-CONF", TripInvariantAuditor.CommitPlaceholder);
            var check = report.Checks.Single(c => c.Id == "AUD-03");
            Assert.Equal(AuditStatus.Fail, check.Status);
            Assert.Contains(check.Findings, f => f.Detail.Contains("not the single current assignment"));
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    [Fact]
    public async Task ContradictoryTerminalReceipts_FailAud06Proxy()
    {
        var connectionString = await CreateAuditDatabaseAsync();
        try
        {
            var tripId = Guid.NewGuid();
            const int round = 1;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            foreach (var type in new[] { "DriverReservedV1", "MatchingFailedV1" })
            {
                await using var receipt = new NpgsqlCommand("""
                    INSERT INTO trip_message_receipts
                        (consumer, message_id, fingerprint, trip_id, message_type, correlation_id,
                         envelope_json, disposition, reason, recorded_at)
                    VALUES (@consumer, @message, @fingerprint, @trip, @type, @correlation,
                            @envelope::jsonb, 'Applied', '', now())
                    """, connection);
                receipt.Parameters.AddWithValue("consumer", type == "DriverReservedV1"
                    ? "trip.driver-state.q" : "trip.matching-outcome.q");
                receipt.Parameters.AddWithValue("message", $"aud-{type}-{Guid.NewGuid():N}");
                receipt.Parameters.AddWithValue("fingerprint", new string('a', 64));
                receipt.Parameters.AddWithValue("trip", tripId);
                receipt.Parameters.AddWithValue("type", type);
                receipt.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString("N"));
                receipt.Parameters.AddWithValue("envelope", JsonSerializer.Serialize(new
                {
                    messageId = $"aud-{type}",
                    messageType = type,
                    payload = new
                    {
                        tripId,
                        driverId = Guid.NewGuid(),
                        reservationId = Guid.NewGuid(),
                        matchingRound = round,
                    },
                }));
                await receipt.ExecuteNonQueryAsync();
            }

            var report = await TripInvariantAuditor.AuditAsync(connectionString,
                AuditorPolicy.QuiescedDefaults, "AUD-TERM", TripInvariantAuditor.CommitPlaceholder);
            var check = report.Checks.Single(c => c.Id == "AUD-06");
            Assert.Equal(AuditStatus.Fail, check.Status);
            Assert.Contains(check.Findings, f => f.Detail.Contains("BOTH"));
        }
        finally
        {
            await DropAsync(connectionString);
        }
    }

    /// <summary>Drives the real compensation path with the real handlers.</summary>
    private static async Task<Guid> DriveCompensatedScenarioAsync(string connectionString)
    {
        var tripId = Guid.NewGuid();
        var riderId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var clock = TimeProvider.System;

        TripDbContext NewContext() => new(new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(connectionString).Options);

        await using (var db = NewContext())
        {
            db.Trips.Add(Trip.Create(tripId, riderId, new(10.7, 106.6), new(10.8, 106.7),
                new("VND", 50000m, "v1", clock.GetUtcNow()), clock.GetUtcNow()));
            await db.SaveChangesAsync();
            await new StartMatchingHandler(db, clock, NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, riderId);
        }

        await using (var db = NewContext())
        {
            var trip = await db.Trips.SingleAsync(t => t.TripId == tripId);
            Assert.True(trip.ApplyDriverReserved(driverId, reservationId, 1, clock.GetUtcNow()).IsSuccess);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var accepted = await new RecordDriverAcceptedHandler(db, clock,
                NullLogger<RecordDriverAcceptedHandler>.Instance).HandleAsync(tripId, driverId, reservationId);
            Assert.True(accepted.IsSuccess);
        }

        await using (var db = NewContext())
        {
            var cancelled = await new CancelRideHandler(db, clock,
                NullLogger<CancelRideHandler>.Instance).HandleAsync(tripId, riderId);
            Assert.True(cancelled.IsSuccess);
        }

        // The exact release completes the reservation obligation...
        var release = MessageEnvelope<JsonElement>.Create("DriverReservationReleasedV1",
            Guid.NewGuid().ToString("N"),
            JsonSerializer.SerializeToElement(new { tripId, driverId, reservationId }));
        await using (var db = NewContext())
        {
            await new TripMessageHandler(db, clock, NullLogger<TripMessageHandler>.Instance)
                .HandleAsync("trip.driver-state.q", release, CancellationToken.None);
        }

        // ...and the terminal DECLINED payment outcome resolves the in-flight
        // authorize with conclusive no-effect evidence, finishing the saga
        // compensation (path).
        await using (var db = NewContext())
        {
            var saga = await db.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId);
            var declined = MessageEnvelope<JsonElement>.Create("PaymentAuthorizationFailedV1",
                Guid.NewGuid().ToString("N"),
                JsonSerializer.SerializeToElement(new { tripId, reasonCode = "DECLINED" }),
                sagaId: saga.SagaId.ToString("D"), operationKey: saga.AuthorizeOperationKey);
            await new TripMessageHandler(db, clock, NullLogger<TripMessageHandler>.Instance)
                .HandleAsync("trip.payment-outcome.q", declined, CancellationToken.None);
        }

        return tripId;
    }

    private static async Task SeedTerminalAssignedTripAsync(string connectionString, bool releaseIntent)
    {
        var tripId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO trips (trip_id, rider_id, state, version, created_at, current_matching_round, updated_at,
                               pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude,
                               fare_currency, fare_amount, fare_policy_version, fare_created_at,
                               assigned_driver_id, reservation_id, matching_round)
            VALUES (@trip, @rider, 'CANCELLED', 3, now(), 1, now(),
                    10.7, 106.6, 10.8, 106.7, 'VND', 50000, 'v1', now(),
                    @driver, @reservation, 1)
            """, connection))
        {
            seed.Parameters.AddWithValue("trip", tripId);
            seed.Parameters.AddWithValue("rider", Guid.NewGuid());
            seed.Parameters.AddWithValue("driver", driverId);
            seed.Parameters.AddWithValue("reservation", reservationId);
            await seed.ExecuteNonQueryAsync();
        }

        if (releaseIntent)
        {
            await using var intent = new NpgsqlCommand("""
                INSERT INTO trip_cleanup_intents
                    (trip_id, key, kind, driver_id, reservation_id, matching_round, created_at)
                VALUES (@trip, @key, 'ReleaseReservation', @driver, @reservation, 1, now())
                """, connection);
            intent.Parameters.AddWithValue("trip", tripId);
            intent.Parameters.AddWithValue("key", $"release:{reservationId:D}");
            intent.Parameters.AddWithValue("driver", driverId);
            intent.Parameters.AddWithValue("reservation", reservationId);
            await intent.ExecuteNonQueryAsync();
        }
    }
}
