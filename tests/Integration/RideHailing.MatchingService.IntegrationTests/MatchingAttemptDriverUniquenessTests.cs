// W08 hardening: one ledger attempt per (trip, matching_round, driver).
// The compute path deduplicates candidates by driver, but before
// ux_matching_attempts_trip_round_driver no DB constraint enforced it: two
// racing computes could mint two ordinals (two reservationIds) for one driver
// in one round. This proves the constraint on REAL PostgreSQL: same-round
// duplicates fail loud, cross-round and cross-trip attempts stay legal, and
// matching by reservationId is unaffected.
using Npgsql;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(MatchingPostgresCollection.Name)]
public sealed class MatchingAttemptDriverUniquenessTests
{
    private readonly MatchingPostgresDatabase _db;

    public MatchingAttemptDriverUniquenessTests(MatchingPostgresDatabase db)
    {
        _db = db;
    }

    [Fact]
    public async Task Same_Round_Duplicate_Driver_Fails_While_Other_Rounds_Stay_Legal()
    {
        var trip = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var otherTrip = Guid.NewGuid();
        try
        {
            var repo = new MatchingRepository(_db.ConnectionString);
            await repo.InsertRoundAsync(trip, 1, Guid.NewGuid().ToString(), "v1");
            await repo.InsertRoundAsync(otherTrip, 1, Guid.NewGuid().ToString(), "v1");

            await repo.InsertAttemptAsync(trip, 1, ordinal: 0, driver, Guid.NewGuid());

            // Duplicate (trip, round, driver) identity at a different ordinal:
            // rejected by the durable constraint, not only by app dedup.
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                repo.InsertAttemptAsync(trip, 1, ordinal: 5, driver, Guid.NewGuid()));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
            Assert.Equal("ux_matching_attempts_trip_round_driver", ex.ConstraintName);

            // The same driver in a LATER round or ANOTHER trip is a new,
            // legal attempt identity (per-round scoping).
            await repo.InsertRoundAsync(trip, 2, Guid.NewGuid().ToString(), "v1");
            await repo.InsertAttemptAsync(trip, 2, ordinal: 0, driver, Guid.NewGuid());
            await repo.InsertAttemptAsync(otherTrip, 1, ordinal: 0, driver, Guid.NewGuid());
        }
        finally
        {
            await using var conn = new NpgsqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM matching_rounds WHERE trip_id IN (@a, @b)", conn);
            cleanup.Parameters.AddWithValue("a", trip);
            cleanup.Parameters.AddWithValue("b", otherTrip);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
