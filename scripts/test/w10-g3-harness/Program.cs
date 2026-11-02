// Verification-only controlled actor for the W10 G3 system proof.
//
// It drives PRODUCTION Trip application handlers (CreateRide,
// TripRideRequestedIntake, RecordDriverAccepted, StartTrip, CompleteTrip)
// against the Trip-owned PostgreSQL database used by the running compose
// service. All cross-service work is left to the real services: Trip outbox
// relay -> RabbitMQ -> Matching/Driver/Payment consumers and back.
//
// This program intentionally does no direct business-row writes and emits no
// authority events. Its only database writes are the handler commits.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;

var traceParent = Environment.GetEnvironmentVariable("TRACEPARENT");
using var operatorActivity = System.Diagnostics.ActivityContext.TryParse(traceParent, null, out _)
    ? new System.Diagnostics.Activity("controlled-lifecycle").SetParentId(traceParent!).Start()
    : null;

var connectionString = Environment.GetEnvironmentVariable("TRIP_DB")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
    ?? "Host=127.0.0.1;Port=5432;Database=trip_db;Username=trip_app;Password=trip_dev_pw";

if (args.Length == 0)
{
    Fail("usage: create <riderId> <idempotencyKey> | reserve <tripId> <round> <driverId> | accept|start|complete <tripId> <driverId> <reservationId> | report <tripId>");
    return;
}

await using var dataSource = NpgsqlDataSource.Create(connectionString);
var options = new DbContextOptionsBuilder<TripDbContext>()
    .UseNpgsql(dataSource)
    .Options;

try
{
    switch (args[0])
    {
        case "create":
            await CreateAsync(args);
            break;
        case "reserve":
            await ReserveAsync(args);
            break;
        case "accept":
            await LifecycleAsync(args, (handler, trip, driver, reservation, ct) =>
                handler.RecordDriverAccepted.HandleAsync(trip, driver, reservation, ct));
            break;
        case "start":
            await LifecycleAsync(args, (handler, trip, driver, reservation, ct) =>
                handler.StartTrip.HandleAsync(trip, driver, reservation, ct));
            break;
        case "complete":
            await LifecycleAsync(args, (handler, trip, driver, reservation, ct) =>
                handler.CompleteTrip.HandleAsync(trip, driver, reservation, ct));
            break;
        case "report":
            await ReportAsync(args);
            break;
        default:
            Fail($"unknown command '{args[0]}'");
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.GetType().Name, message = ex.Message }));
    Environment.ExitCode = 2;
}

async Task CreateAsync(string[] a)
{
    if (a.Length != 3 || !Guid.TryParse(a[1], out var riderId) || riderId == Guid.Empty)
    {
        Fail("create requires <riderId> <idempotencyKey>");
        return;
    }

    await using var db = new TripDbContext(options);
    var clock = TimeProvider.System;
    var create = new CreateRideHandler(db, clock);
    var result = await create.HandleAsync(new CreateRideCommand(
        riderId,
        new TripLocation(10.7769, 106.7009),
        new TripLocation(10.8231, 106.6297),
        a[2]));

    if (!result.IsSuccess)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            error = result.Error.ToString(),
            message = result.ErrorMessage,
        }));
        Environment.ExitCode = 2;
        return;
    }

    var intake = new TripRideRequestedIntake(
        db,
        new TripMessagingSettings(true),
        clock,
        NullLogger<TripRideRequestedIntake>.Instance);
    var staged = await intake.EnsureRideRequestedAsync(result.TripId, riderId, CancellationToken.None);
    if (!staged)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            error = "MATCHING_INTENT_PENDING",
            tripId = result.TripId,
        }));
        Environment.ExitCode = 2;
        return;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        command = "create",
        tripId = result.TripId,
        isReplay = result.IsReplay,
    }));
}

// Runs the PRODUCTION Matching reserve orchestrator against the durable round
// row created by the real RideRequestedV1 consumer. Matching owns the
// reservation identity; it calls Driver over the same HTTP contract as the
// service and marks the round RESERVED only from durable attempt truth.
async Task ReserveAsync(string[] a)
{
    if (a.Length != 4 || !Guid.TryParse(a[1], out var tripId) || tripId == Guid.Empty
        || !int.TryParse(a[2], out var round) || round < 1
        || !Guid.TryParse(a[3], out var driverId) || driverId == Guid.Empty)
    {
        Fail("reserve requires <tripId> <round> <driverId>");
        return;
    }

    var matchingConnectionString = Environment.GetEnvironmentVariable("MATCHING_DB")
        ?? "Host=127.0.0.1;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw";
    var driverBaseUrl = Environment.GetEnvironmentVariable("DRIVER_BASE_URL")
        ?? "http://127.0.0.1:8102";

    await using var matchingDataSource = NpgsqlDataSource.Create(matchingConnectionString);
    string? inputMessageId;
    await using (var connection = await matchingDataSource.OpenConnectionAsync())
    await using (var command = new NpgsqlCommand(
        "SELECT input_message_id FROM matching_rounds WHERE trip_id = @trip AND matching_round = @round",
        connection))
    {
        command.Parameters.AddWithValue("trip", tripId);
        command.Parameters.AddWithValue("round", round);
        inputMessageId = (string?)await command.ExecuteScalarAsync();
    }

    if (string.IsNullOrWhiteSpace(inputMessageId))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            error = "ROUND_NOT_FOUND",
            tripId,
            round,
        }));
        Environment.ExitCode = 2;
        return;
    }

    var attempts = new MatchingAttemptRepository(matchingConnectionString);
    var terminalizer = new MatchingTerminalizer(
        matchingDataSource,
        new NpgsqlOutboxStore(matchingDataSource),
        TimeProvider.System);
    using var http = new HttpClient
    {
        BaseAddress = new Uri(driverBaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(15),
    };
    var orchestrator = new MatchingReserveOrchestrator(
        attempts,
        new HttpDriverClient(http),
        terminalizer);

    var result = await orchestrator.ExecuteRoundAsync(
        tripId, round, new[] { driverId }, inputMessageId, CancellationToken.None);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = result.Outcome == RoundOutcome.Reserved,
        command = "reserve",
        tripId,
        round,
        inputMessageId,
        outcome = result.Outcome.ToString(),
        driverId = result.DriverId,
        reservationId = result.ReservationId,
        code = result.Code,
        resultMessageId = result.ResultMessageId,
    }));
    if (result.Outcome != RoundOutcome.Reserved) Environment.ExitCode = 2;
}

async Task LifecycleAsync(string[] a, Func<LifecycleHandlers, Guid, Guid, Guid, CancellationToken, Task<TripLifecycleResult>> run)
{
    if (a.Length != 4 || !Guid.TryParse(a[1], out var tripId) || !Guid.TryParse(a[2], out var driverId)
        || !Guid.TryParse(a[3], out var reservationId)
        || tripId == Guid.Empty || driverId == Guid.Empty || reservationId == Guid.Empty)
    {
        Fail($"{a[0]} requires <tripId> <driverId> <reservationId>");
        return;
    }

    await using var db = new TripDbContext(options);
    var handlers = new LifecycleHandlers(
        new RecordDriverAcceptedHandler(db, TimeProvider.System, NullLogger<RecordDriverAcceptedHandler>.Instance),
        new StartTripHandler(db, TimeProvider.System, NullLogger<StartTripHandler>.Instance),
        new CompleteTripHandler(db, TimeProvider.System, NullLogger<CompleteTripHandler>.Instance));

    var result = await run(handlers, tripId, driverId, reservationId, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = result.IsSuccess,
        command = a[0],
        tripId,
        state = result.IsSuccess ? result.State.ToCanonicalName() : null,
        isReplay = result.IsReplay,
        error = result.IsSuccess ? null : result.ErrorCode.ToString(),
        message = result.IsSuccess ? null : result.Message,
    }));
    if (!result.IsSuccess) Environment.ExitCode = 2;
}

async Task ReportAsync(string[] a)
{
    if (a.Length != 2 || !Guid.TryParse(a[1], out var tripId) || tripId == Guid.Empty)
    {
        Fail("report requires <tripId>");
        return;
    }

    await using var db = new TripDbContext(options);
    var trip = await db.Trips.AsNoTracking().SingleOrDefaultAsync(t => t.TripId == tripId);
    if (trip is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, tripId, exists = false }));
        return;
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    var saga = await QueryOneAsync(connection, """
        SELECT saga_id::text, status, current_step, step_version, reservation_id::text,
               authorize_operation_key, capture_operation_key, void_operation_key,
               last_error_code, requires_review_reason, created_at, updated_at
        FROM ride_sagas WHERE trip_id = @id
        """, tripId);
    var cleanup = await QueryManyAsync(connection, """
        SELECT key, kind, driver_id::text, reservation_id::text, matching_round, completed_at
        FROM trip_cleanup_intents WHERE trip_id = @id ORDER BY key
        """, tripId);
    var outbox = await QueryManyAsync(connection, """
        SELECT message_id, message_type, status, attempt_count, operation_key
        FROM outbox_messages
        WHERE aggregate_id = @text OR payload_json->>'tripId' = @text
        ORDER BY occurred_at, message_id
        """, tripId, text: tripId.ToString("D"));
    var receipts = await QueryManyAsync(connection, """
        SELECT consumer, message_type, disposition, reason
        FROM trip_message_receipts WHERE trip_id = @id ORDER BY recorded_at, consumer
        """, tripId);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        trip = new
        {
            tripId,
            riderId = trip.RiderId,
            state = trip.State.ToCanonicalName(),
            version = trip.Version,
            matchingRound = trip.CurrentMatchingRound,
            driverId = trip.Assignment?.DriverId,
            reservationId = trip.Assignment?.ReservationId,
            fare = new
            {
                amount = trip.Fare.Amount,
                currency = trip.Fare.Currency,
                policyVersion = trip.Fare.PricingPolicyVersion,
            },
        },
        saga,
        cleanup,
        outbox,
        receipts,
    }));
}

async Task<Dictionary<string, object?>?> QueryOneAsync(NpgsqlConnection connection, string sql, Guid id)
{
    var rows = await QueryManyAsync(connection, sql, id);
    return rows.Count == 0 ? null : rows[0];
}

async Task<List<Dictionary<string, object?>>> QueryManyAsync(
    NpgsqlConnection connection, string sql, Guid id, string? text = null)
{
    await using var command = new NpgsqlCommand(sql, connection);
    if (sql.Contains("@id", StringComparison.Ordinal)) command.Parameters.AddWithValue("id", id);
    if (text is not null) command.Parameters.AddWithValue("text", text);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
            {
                Guid value => value.ToString("D"),
                DateTimeOffset value => value.ToString("O"),
                DateTime value => value.ToString("O"),
                decimal value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => reader.GetValue(i),
            };
        }
        rows.Add(row);
    }
    return rows;
}

static void Fail(string message)
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "USAGE", message }));
    Environment.Exit(2);
}

internal sealed record LifecycleHandlers(
    RecordDriverAcceptedHandler RecordDriverAccepted,
    StartTripHandler StartTrip,
    CompleteTripHandler CompleteTrip);
