// Two-writer optimistic-concurrency proof (A-W03-07) on real PostgreSQL.
//
// Deterministic stale-write fencing: two independent DbContexts load the same
// MATCHING row before either save, each applies a different legal transition,
// the winner commits and the loser receives the typed Trip conflict. No
// Task.Delay/sleep: staleness comes from independent tracking, not timing.
// This proves LOCAL optimistic concurrency only — never Driver exclusivity,
// stale-round correctness, cross-service agreement or exactly-once.

using Microsoft.EntityFrameworkCore;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripConcurrencyComponentTests
{
    private readonly TripPostgresFixture _fixture;

    public TripConcurrencyComponentTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private DbContextOptions<TripDbContext> Options() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;

    [Fact]
    public async Task TwoWriter_StaleSave_ConflictsWithTypedException_AndKeepsSingleWinner()
    {
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        var tripId = Guid.NewGuid();
        var riderId = Guid.NewGuid();
        var winnerDriverId = Guid.NewGuid();
        var winnerReservationId = Guid.NewGuid();
        var pickup = new TripLocation(10.762622, 106.660172);
        var dropoff = new TripLocation(10.776889, 106.700806);
        var fare = new FareSnapshot(
            "VND", 50000m, "p0-test",
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));

        await using (var seed = new TripDbContext(options))
        {
            var initial = Trip.Reconstitute(
                tripId, riderId, pickup, dropoff, fare,
                TripState.Matching, null,
                new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero),
                0);
            seed.Trips.Add(initial);
            await seed.SaveChangesAsync();
        }

        await using var dbA = new TripDbContext(options);
        await using var dbB = new TripDbContext(options);

        var tripA = await dbA.Trips.SingleAsync(t => t.TripId == tripId);
        var tripB = await dbB.Trips.SingleAsync(t => t.TripId == tripId);

        Assert.Equal(tripA.Version, tripB.Version);
        var originalVersion = tripA.Version;

        Assert.True(tripA.ApplyDriverReserved(winnerDriverId, winnerReservationId, 1).IsSuccess);
        Assert.True(tripB.MarkMatchingFailed().IsSuccess);

        await dbA.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<TripConcurrencyConflictException>(
            () => dbB.SaveChangesAsync());
        Assert.Equal(tripId, conflict.TripId);

        await using (var verify = new TripDbContext(options))
        {
            var authoritative = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);

            Assert.Equal(TripState.DriverReserved, authoritative.State);
            Assert.Equal(originalVersion + 1, authoritative.Version);
            Assert.NotNull(authoritative.Assignment);
            Assert.Equal(winnerDriverId, authoritative.Assignment.DriverId);
            Assert.Equal(winnerReservationId, authoritative.Assignment.ReservationId);
            Assert.Equal(1, authoritative.Assignment.MatchingRound);
        }
    }
}
