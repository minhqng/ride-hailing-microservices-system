using Npgsql;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingCancellationStatusTests(MatchingPostgresDatabase database)
{
    [Fact]
    public async Task ProductionIntakePreservesFirstValidTraceContextAcrossReplay()
    {
        var trip = Guid.NewGuid();
        var attempts = new MatchingAttemptRepository(database.ConnectionString);
        var parent = "00-" + Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N")[..16] + "-01";
        var first = await attempts.EnsureProductionRoundAsync(trip, 1, "trace-" + trip, 106.7, 10.7, traceParent: parent);
        var replay = await attempts.EnsureProductionRoundAsync(trip, 1, "trace-" + trip, 106.7, 10.7, traceParent: null);
        Assert.Equal(parent, first.IntakeTraceParent);
        Assert.Equal(parent, replay.IntakeTraceParent);
    }

    [Fact]
    public async Task CancellationBeforeIntakeRemainsStoppedWhenProductionRoundArrives()
    {
        var trip = Guid.NewGuid();
        var work = new MatchingWorkRepository(database.ConnectionString);
        Assert.False(await work.IsCancellationStoppedAsync(trip, 1, default));
        await work.RecordCancelIntentAsync(trip, 1);
        Assert.True(await work.IsCancellationStoppedAsync(trip, 1, default));
        var attempts = new MatchingAttemptRepository(database.ConnectionString);
        var round = await attempts.EnsureProductionRoundAsync(trip, 1, Guid.NewGuid().ToString(), 106.7, 10.7);
        Assert.True(round.CancelRequested);
        Assert.True(await work.IsCancellationStoppedAsync(trip, 1, default));
        Assert.False(await work.IsCancellationStoppedAsync(trip, 2, default));
    }

    [Theory]
    [InlineData("READY")]
    [InlineData("IN_FLIGHT")]
    [InlineData("UNKNOWN")]
    public async Task UnresolvedAttemptPreventsConclusiveCancellation(string state)
    {
        var trip = Guid.NewGuid();
        var attempts = new MatchingAttemptRepository(database.ConnectionString);
        await attempts.EnsureProductionRoundAsync(trip, 1, Guid.NewGuid().ToString(), 106.7, 10.7);
        await using (var command = database.DataSource.CreateCommand("""
            INSERT INTO matching_attempts (trip_id,matching_round,ordinal,driver_id,reservation_id,state)
            VALUES (@trip,1,0,@driver,@reservation,@state);
            """))
        {
            command.Parameters.AddWithValue("trip", trip);
            command.Parameters.AddWithValue("driver", Guid.NewGuid());
            command.Parameters.AddWithValue("reservation", Guid.NewGuid());
            command.Parameters.AddWithValue("state", state);
            await command.ExecuteNonQueryAsync();
        }
        var work = new MatchingWorkRepository(database.ConnectionString);
        await work.RecordCancelIntentAsync(trip, 1);
        Assert.False(await work.IsCancellationStoppedAsync(trip, 1, default));
        await using (var command = database.DataSource.CreateCommand("""
            UPDATE matching_attempts SET state='REJECTED' WHERE trip_id=@trip;
            """))
        {
            command.Parameters.AddWithValue("trip", trip);
            await command.ExecuteNonQueryAsync();
        }
        Assert.True(await work.IsCancellationStoppedAsync(trip, 1, default));
    }

    [Fact]
    public async Task ReservationConvergenceClosesUnselectedReadyAttempts()
    {
        var trip = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var winnerReservation = Guid.NewGuid();
        var attempts = new MatchingAttemptRepository(database.ConnectionString);
        await attempts.EnsureProductionRoundAsync(trip, 1, Guid.NewGuid().ToString(), 106.7, 10.7);
        await InsertAttemptAsync(trip, ordinal: 0, winner, winnerReservation, "RESERVED");
        await InsertAttemptAsync(trip, ordinal: 1, Guid.NewGuid(), Guid.NewGuid(), "READY");

        Assert.True(await attempts.TryMarkRoundReservedAsync(trip, 1, winner, winnerReservation, default));

        Assert.Equal(["RESERVED", "CANCELLED"], await AttemptStatesAsync(trip));
        Assert.False(await new MatchingWorkRepository(database.ConnectionString)
            .IsCancellationStoppedAsync(trip, 1, default));
    }

    [Fact]
    public async Task CancellationAfterSettledReservationClosesUnselectedReadyAttempts()
    {
        var trip = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var winnerReservation = Guid.NewGuid();
        var attempts = new MatchingAttemptRepository(database.ConnectionString);
        await attempts.EnsureProductionRoundAsync(trip, 1, Guid.NewGuid().ToString(), 106.7, 10.7);
        await InsertAttemptAsync(trip, ordinal: 0, winner, winnerReservation, "RESERVED");
        await InsertAttemptAsync(trip, ordinal: 1, Guid.NewGuid(), Guid.NewGuid(), "READY");
        // A settled round left behind by an older process: the winner is
        // already RESERVED but an un-dispatched READY row still exists.
        await using (var command = database.DataSource.CreateCommand("""
            UPDATE matching_rounds
            SET status='RESERVED', selected_driver_id=@driver, selected_reservation_id=@reservation
            WHERE trip_id=@trip AND matching_round=1;
            """))
        {
            command.Parameters.AddWithValue("trip", trip);
            command.Parameters.AddWithValue("driver", winner);
            command.Parameters.AddWithValue("reservation", winnerReservation);
            await command.ExecuteNonQueryAsync();
        }

        var work = new MatchingWorkRepository(database.ConnectionString);
        await work.RecordCancelIntentAsync(trip, 1);

        Assert.Equal(["RESERVED", "CANCELLED"], await AttemptStatesAsync(trip));
        Assert.True(await work.IsCancellationStoppedAsync(trip, 1, default));
    }

    private async Task InsertAttemptAsync(
        Guid trip, int ordinal, Guid driver, Guid reservation, string state)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO matching_attempts (trip_id,matching_round,ordinal,driver_id,reservation_id,state)
            VALUES (@trip,1,@ordinal,@driver,@reservation,@state);
            """);
        command.Parameters.AddWithValue("trip", trip);
        command.Parameters.AddWithValue("ordinal", ordinal);
        command.Parameters.AddWithValue("driver", driver);
        command.Parameters.AddWithValue("reservation", reservation);
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> AttemptStatesAsync(Guid trip)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT state FROM matching_attempts WHERE trip_id=@trip ORDER BY ordinal;
            """);
        command.Parameters.AddWithValue("trip", trip);
        await using var reader = await command.ExecuteReaderAsync();
        var states = new List<string>();
        while (await reader.ReadAsync())
        {
            states.Add(reader.GetString(0));
        }

        return [.. states];
    }
}
