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
}
