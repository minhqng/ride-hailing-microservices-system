using System.Text.Json;
using Npgsql;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

namespace RideHailing.DriverService.IntegrationTests;

[Collection(DriverBrokerCollection.Name)]
public sealed class DriverLifecycleTests(DriverPostgresDatabase db)
{
    internal static MessageEnvelope<JsonElement> Command(string type, Guid driver, Guid trip,
        Guid reservation, string? id = null) => MessageEnvelope<JsonElement>.Create(type,
            trip.ToString("N"), JsonSerializer.SerializeToElement(new
            { driverId = driver, tripId = trip, reservationId = reservation, matchingRound = 1 }), messageId: id);

    private async Task<(Guid Driver, Guid Trip, Guid Reservation)> ReserveAsync()
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        var repo = db.CreateRepository();
        Assert.True(await repo.TryInsertAsync(driver));
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won,
            (await repo.TryReserveAsync(driver.DriverId, trip, reservation, driver.Version, matchingRound: 1)).Outcome);
        return (driver.DriverId, trip, reservation);
    }

    private async Task<long> CountAsync(string table, Guid driver)
    {
        // Table identifiers are fixed test-owned literals from callers.
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE " +
            (table == "outbox_messages" ? "aggregate_id = @id" : "driver_id = @id"), conn);
        cmd.Parameters.AddWithValue("id", table == "outbox_messages" ? (object)driver.ToString("N") : driver);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task StartComplete_RestartAndHistoricalReplay_PreserveCanonicalIdentity()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var start = await db.CreateRepository().TryMarkTripStartedAsync(driver, trip, reservation);
        Assert.Equal(new DriverLifecycleResult("Applied", 4,
            DriverEventMessageIds.ForDriverEffect(driver, reservation, 4, "ON_TRIP")), start);
        var onTrip = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(DriverStatus.OnTrip, onTrip.Status);
        Assert.Equal(trip, onTrip.ActiveTripId);
        Assert.Null(onTrip.ReservationId);
        Assert.Null(onTrip.ReservationRound);
        var complete = await db.CreateRepository().TryMarkTripCompletedAsync(driver, trip, reservation);
        Assert.Equal("Applied", complete.Disposition);
        Assert.Equal(5, complete.Version);
        var available = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(DriverStatus.Available, available.Status);
        Assert.Null(available.ActiveTripId);
        Assert.Null(available.ReservationId);
        Assert.Equal(start with { Disposition = "Replayed" },
            await db.CreateRepository().TryMarkTripStartedAsync(driver, trip, reservation));
        Assert.Equal(complete with { Disposition = "Replayed" },
            await db.CreateRepository().TryMarkTripCompletedAsync(driver, trip, reservation));
        var nextTrip = Guid.NewGuid();
        var nextReservation = Guid.NewGuid();
        Assert.Equal(ReserveOutcome.Won, (await db.CreateRepository().TryReserveAsync(
            driver, nextTrip, nextReservation, available.Version, matchingRound: 2)).Outcome);
        Assert.Equal("Replayed", (await db.CreateRepository().TryMarkTripStartedAsync(driver, trip, reservation)).Disposition);
        Assert.Equal(DriverStatus.Reserved, (await db.CreateRepository().LoadAsync(driver))!.Status);
        await db.CreateRepository().TryMarkTripStartedAsync(driver, nextTrip, nextReservation);
        Assert.Equal("Replayed", (await db.CreateRepository().TryMarkTripCompletedAsync(driver, trip, reservation)).Disposition);
        var newer = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(nextTrip, newer.ActiveTripId);
        Assert.Equal(7, newer.Version);
        await db.CreateRepository().TryMarkTripCompletedAsync(driver, nextTrip, nextReservation);
        Assert.Equal(4, await CountAsync("driver_lifecycle_effects", driver));
        Assert.Equal(6, await CountAsync("outbox_messages", driver));
        foreach (var (result, expectedTrip, expectedReservation, status) in new[]
        { (start, trip, reservation, "ON_TRIP"), (complete, trip, reservation, "AVAILABLE") })
        {
            var message = (await db.OutboxStore.LoadAsync(result.EventMessageId!))!;
            var payload = message.ToEnvelope().Payload;
            Assert.Equal(expectedTrip, payload.GetProperty("tripId").GetGuid());
            Assert.Equal(expectedReservation, payload.GetProperty("reservationId").GetGuid());
            Assert.Equal(driver, payload.GetProperty("driverId").GetGuid());
            Assert.Equal(status, payload.GetProperty("status").GetString());
            Assert.Equal(result.Version, message.AggregateVersion);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task WrongIdentity_DoesNotChangeStateVersionOrIntent(bool completed, bool wrongTrip)
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var repo = db.CreateRepository();
        if (completed) await repo.TryMarkTripStartedAsync(driver, trip, reservation);
        var before = (await repo.LoadAsync(driver))!;
        var count = await CountAsync("outbox_messages", driver);
        var result = completed
            ? await repo.TryMarkTripCompletedAsync(driver, wrongTrip ? Guid.NewGuid() : trip,
                wrongTrip ? reservation : Guid.NewGuid())
            : await repo.TryMarkTripStartedAsync(driver, wrongTrip ? Guid.NewGuid() : trip,
                wrongTrip ? reservation : Guid.NewGuid());
        Assert.Equal("Rejected", result.Disposition);
        var after = (await repo.LoadAsync(driver))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(count, await CountAsync("outbox_messages", driver));
    }

    [Fact]
    public async Task OldUnstartedReservation_CannotHijackNewReservation()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var repo = db.CreateRepository();
        await repo.TryReleaseAsync(driver, trip, reservation);
        var nextTrip = Guid.NewGuid();
        var nextReservation = Guid.NewGuid();
        await repo.TryReserveAsync(driver, nextTrip, nextReservation, 4);
        Assert.Equal("Rejected", (await repo.TryMarkTripStartedAsync(driver, trip, reservation)).Disposition);
        Assert.Equal(nextReservation, (await repo.LoadAsync(driver))!.ReservationId);
        Assert.Equal(3, await CountAsync("outbox_messages", driver));
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("AVAILABLE")]
    [InlineData("ON_TRIP")]
    public async Task NonReservedOrLegacyRows_FailSafely(string state)
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        var trip = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        if (state != "OFFLINE") driver.GoOnline(driver.Version);
        if (state == "ON_TRIP")
        {
            driver.Reserve(trip, reservation, driver.Version);
            driver.MarkTripStarted(trip, reservation);
        }
        var repo = db.CreateRepository();
        await repo.TryInsertAsync(driver);
        Assert.Equal("Rejected", (await repo.TryMarkTripStartedAsync(driver.DriverId, trip, reservation)).Disposition);
        Assert.Equal("LIFECYCLE_PROVENANCE_REQUIRED",
            (await repo.TryMarkTripCompletedAsync(driver.DriverId, trip, reservation)).Reason);
        Assert.Equal(driver.Version, (await repo.LoadAsync(driver.DriverId))!.Version);
        Assert.Equal(0, await CountAsync("outbox_messages", driver.DriverId));
    }

    [Fact]
    public async Task HistoricalIdentities_CannotBeNewlyReserved()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var repo = db.CreateRepository();
        await repo.TryMarkTripStartedAsync(driver, trip, reservation);
        await repo.TryMarkTripCompletedAsync(driver, trip, reservation);
        foreach (var (t, r) in new[] { (trip, Guid.NewGuid()), (Guid.NewGuid(), reservation) })
            Assert.Equal(ReserveOutcome.Conflict, (await repo.TryReserveAsync(driver, t, r, 5)).Outcome);
        Assert.Equal(5, (await repo.LoadAsync(driver))!.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDuplicates_CommitOneEffectAndStableReceipt(bool completed)
    {
        var (driver, trip, reservation) = await ReserveAsync();
        if (completed) await db.CreateRepository().TryMarkTripStartedAsync(driver, trip, reservation);
        var command = Command(completed ? DriverCommand.Complete : DriverCommand.Start, driver, trip, reservation);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<DriverLifecycleResult> Send()
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await db.CreateRepository().HandleCommandAsync(command);
        }
        var calls = Enumerable.Range(0, 4).Select(_ => Send()).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.Equal(completed ? 5 : 4, (await db.CreateRepository().LoadAsync(driver))!.Version);
        Assert.Equal(completed ? 3 : 2, await CountAsync("outbox_messages", driver));
        Assert.Equal("Replayed", (await db.CreateRepository().HandleCommandAsync(command with
            { MessageId = Guid.NewGuid().ToString("N") })).Disposition);
    }

    [Fact]
    public async Task ConcurrentStartAndRelease_HaveOneLegalWinner()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<DriverLifecycleResult> Send(string type)
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await db.CreateRepository().HandleCommandAsync(Command(type, driver, trip, reservation));
        }
        var start = Send(DriverCommand.Start);
        var release = Send(DriverCommand.Release);
        gate.SetResult();
        var results = await Task.WhenAll(start, release).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(results, r => r.Disposition == "Applied");
        Assert.Single(results, r => r.Disposition == "Rejected");
        var current = (await db.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(4, current.Version);
        Assert.Contains(current.Status, new[] { DriverStatus.Available, DriverStatus.OnTrip });
        Assert.Null(current.ReservationId);
        Assert.Equal(2, await CountAsync("outbox_messages", driver));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameMessageIdWithAlteredContent_IsPoisonWithoutMutation(bool alteredPayload)
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var command = Command(DriverCommand.Start, driver, trip, reservation);
        var result = await db.CreateRepository().HandleCommandAsync(command);
        var changed = alteredPayload
            ? Command(DriverCommand.Start, driver, Guid.NewGuid(), reservation, command.MessageId)
            : Command(DriverCommand.Complete, driver, trip, reservation, command.MessageId);
        var error = await Assert.ThrowsAsync<DeliveryFailureException>(() => db.CreateRepository().HandleCommandAsync(changed));
        Assert.Equal("DRIVER_COMMAND_CONTENT_CONFLICT", error.Code);
        Assert.Equal(result, await db.CreateRepository().HandleCommandAsync(command));
        Assert.Equal(4, (await db.CreateRepository().LoadAsync(driver))!.Version);
    }

    [Theory]
    [InlineData("outbox_messages", false)]
    [InlineData("outbox_messages", true)]
    [InlineData("driver_command_receipts", false)]
    [InlineData("driver_command_receipts", true)]
    public async Task RequiredInsertFailure_RollsBackStateLedgerReceiptAndOutbox(string table, bool completed)
    {
        var (driver, trip, reservation) = await ReserveAsync();
        var repo = db.CreateRepository();
        if (completed) await repo.TryMarkTripStartedAsync(driver, trip, reservation);
        var command = Command(completed ? DriverCommand.Complete : DriverCommand.Start, driver, trip, reservation);
        await SqlAsync($"""
            CREATE FUNCTION fail_lifecycle_insert() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected required insert failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_lifecycle_insert BEFORE INSERT ON {table}
            FOR EACH ROW EXECUTE FUNCTION fail_lifecycle_insert();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => repo.HandleCommandAsync(command));
            Assert.Equal(completed ? 4 : 3, (await repo.LoadAsync(driver))!.Version);
            Assert.Equal(completed ? 1 : 0, await CountAsync("driver_lifecycle_effects", driver));
            Assert.Equal(completed ? 2 : 1, await CountAsync("outbox_messages", driver));
            await using var conn = await db.DataSource.OpenConnectionAsync();
            await using var receipt = new NpgsqlCommand("SELECT count(*) FROM driver_command_receipts WHERE message_id = @id", conn);
            receipt.Parameters.AddWithValue("id", command.MessageId);
            Assert.Equal(0L, await receipt.ExecuteScalarAsync());
        }
        finally { await SqlAsync($"DROP TRIGGER fail_lifecycle_insert ON {table}; DROP FUNCTION fail_lifecycle_insert();"); }
        Assert.Equal("Applied", (await repo.HandleCommandAsync(command)).Disposition);
    }

    [Fact]
    public async Task CommandEvent_PreservesCorrelationCausationSagaAndTrace()
    {
        var (driver, trip, reservation) = await ReserveAsync();
        using var activity = new System.Diagnostics.Activity("lifecycle-test").Start();
        var command = Command(DriverCommand.Start, driver, trip, reservation) with { SagaId = "saga", OperationKey = "start" };
        var result = await db.CreateRepository().HandleCommandAsync(command);
        var message = (await db.OutboxStore.LoadAsync(result.EventMessageId!))!;
        Assert.Equal(command.CorrelationId, message.CorrelationId);
        Assert.Equal(command.MessageId, message.CausationId);
        Assert.Equal(command.SagaId, message.SagaId);
        Assert.Equal(command.OperationKey, message.OperationKey);
        Assert.Contains(activity.TraceId.ToString(), message.HeadersJson);
    }

    private async Task SqlAsync(string sql)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
