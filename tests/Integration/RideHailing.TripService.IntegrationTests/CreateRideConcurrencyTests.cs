// Deterministic CreateRide 2-way race proofs against real PostgreSQL 18.x.
//
// Each scenario runs 5 independent iterations with fresh Rider identities
// and keys. Both competing logical requests execute on SEPARATE TripDbContext
// instances (separate handler instances, no shared tracked state) and
// rendezvous at a TEST-ONLY SaveChangesInterceptor barrier, so both pre-reads
// provably observe the initial state before either authoritative transaction
// saves. No sleeps, no timing assumptions; PostgreSQL unique guards (the
// idempotency composite key, the active-Rider partial index) arbitrate the
// writes and the handler reconciles the loser from committed truth.
//
// Scenarios:
//   A. same key + same semantic request -> both SUCCESS, one TripId, replay.
//   B. same key + conflicting request  -> one success + IDEMPOTENCY_CONFLICT.
//   C. different keys + same Rider      -> one success + ACTIVE_TRIP_CONFLICT.

using Microsoft.EntityFrameworkCore;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class CreateRideConcurrencyTests
{
    private const int Iterations = 5;

    private readonly TripPostgresFixture _fixture;

    public CreateRideConcurrencyTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static TripLocation Pickup() => new(10.762622, 106.660172);

    private static TripLocation Dropoff() => new(10.776889, 106.700806);

    private static TripLocation AltDropoff() => new(11.0, 107.0);

    private DbContextOptions<TripDbContext> BarrierOptions(TwoPartySaveBarrier barrier) =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(new SaveBarrierInterceptor(barrier))
            .Options;

    private DbContextOptions<TripDbContext> PlainOptions() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;

    private async Task MigrateAsync()
    {
        await using var setup = new TripDbContext(PlainOptions());
        await setup.Database.MigrateAsync();
    }

    private static async Task<CreateRideResult> RunAsync(
        DbContextOptions<TripDbContext> options,
        CreateRideCommand command)
    {
        await using var db = new TripDbContext(options);
        return await new CreateRideHandler(db, new FixedTimeProvider(FixedNow)).HandleAsync(command);
    }

    [Fact]
    public async Task SameKey_SameRequest_Race_ConvergesToOneTrip_WithReplay()
    {
        await MigrateAsync();

        for (var i = 0; i < Iterations; i++)
        {
            var rider = Guid.NewGuid();
            var key = $"race-same-{i}-{Guid.NewGuid():N}";
            var scope = CreateRideRequestFingerprint.BuildScope(rider);
            var barrier = new TwoPartySaveBarrier();
            var optionsA = BarrierOptions(barrier);
            var optionsB = BarrierOptions(barrier);
            var command = new CreateRideCommand(rider, Pickup(), Dropoff(), key);

            var taskA = RunAsync(optionsA, command);
            var taskB = RunAsync(optionsB, command);
            var results = await Task.WhenAll(taskA, taskB);

            Assert.True(results[0].IsSuccess);
            Assert.True(results[1].IsSuccess);
            Assert.Equal(results[0].TripId, results[1].TripId);
            Assert.NotEqual(Guid.Empty, results[0].TripId);
            // Exactly one side created; the other reconciled from committed truth.
            Assert.True(results[0].IsReplay ^ results[1].IsReplay);

            var winner = results[0].IsReplay ? results[1] : results[0];
            Assert.False(winner.IsReplay);

            await using var verify = new TripDbContext(PlainOptions());
            var trips = await verify.Trips.AsNoTracking()
                .Where(t => t.RiderId == rider).ToListAsync();
            var trip = Assert.Single(trips);
            Assert.Equal(results[0].TripId, trip.TripId);

            var records = await verify.RequestIdempotency.AsNoTracking()
                .Where(r => r.Scope == scope).ToListAsync();
            var record = Assert.Single(records);
            Assert.Equal(key, record.Key);
            Assert.Equal(trip.TripId, record.ResourceId);
            Assert.Equal(
                CreateRideRequestFingerprint.Compute(rider, Pickup(), Dropoff()),
                record.RequestFingerprint);

            // DSI-014 oracle: both callers resolve to the SAME persisted
            // FareSnapshot; the winner's committed snapshot is authoritative.
            var authoritative = await verify.Trips.AsNoTracking()
                .SingleAsync(t => t.TripId == trip.TripId);
            Assert.Equal("VND", authoritative.Fare.Currency);
            Assert.Equal("p0-w4-v1", authoritative.Fare.PricingPolicyVersion);
            Assert.Equal(
                DeterministicFareCalculator.Calculate(Pickup(), Dropoff(), FixedNow).Amount,
                authoritative.Fare.Amount);
            Assert.Equal(trip.Fare, authoritative.Fare);
        }
    }

    [Fact]
    public async Task SameKey_DifferentRequest_Race_OneSuccess_OneIdempotencyConflict()
    {
        await MigrateAsync();

        for (var i = 0; i < Iterations; i++)
        {
            var rider = Guid.NewGuid();
            var key = $"race-conflict-{i}-{Guid.NewGuid():N}";
            var scope = CreateRideRequestFingerprint.BuildScope(rider);
            var barrier = new TwoPartySaveBarrier();
            var commandX = new CreateRideCommand(rider, Pickup(), Dropoff(), key);
            var commandY = new CreateRideCommand(rider, Pickup(), AltDropoff(), key);

            var taskX = RunAsync(BarrierOptions(barrier), commandX);
            var taskY = RunAsync(BarrierOptions(barrier), commandY);
            var results = await Task.WhenAll(taskX, taskY);

            var successes = results.Where(r => r.IsSuccess).ToList();
            var failures = results.Where(r => !r.IsSuccess).ToList();
            var success = Assert.Single(successes);
            var failure = Assert.Single(failures);
            Assert.False(success.IsReplay);
            // Idempotency semantic conflict takes precedence over the
            // winner's now-active Trip: the loser must NOT see ACTIVE_TRIP_CONFLICT.
            Assert.Equal(CreateRideError.IdempotencyConflict, failure.Error);

            await using var verify = new TripDbContext(PlainOptions());
            var trips = await verify.Trips.AsNoTracking()
                .Where(t => t.RiderId == rider).ToListAsync();
            var trip = Assert.Single(trips);
            Assert.Equal(success.TripId, trip.TripId);

            var records = await verify.RequestIdempotency.AsNoTracking()
                .Where(r => r.Scope == scope).ToListAsync();
            var record = Assert.Single(records);
            Assert.Equal(key, record.Key);
            Assert.Equal(success.TripId, record.ResourceId);

            var winnerCommand = ReferenceEquals(success, results[0]) ? commandX : commandY;
            Assert.Equal(
                CreateRideRequestFingerprint.Compute(rider, winnerCommand.Pickup, winnerCommand.Dropoff),
                record.RequestFingerprint);

            var authoritative = await verify.Trips.AsNoTracking()
                .SingleAsync(t => t.TripId == success.TripId);
            Assert.Equal(trip.Fare, authoritative.Fare);
        }
    }

    [Fact]
    public async Task DifferentKeys_SameRider_Race_OneSuccess_OneActiveConflict()
    {
        await MigrateAsync();

        for (var i = 0; i < Iterations; i++)
        {
            var rider = Guid.NewGuid();
            var suffix = $"{i}-{Guid.NewGuid():N}";
            var keyA = $"race-k1-{suffix}";
            var keyB = $"race-k2-{suffix}";
            var scope = CreateRideRequestFingerprint.BuildScope(rider);
            var barrier = new TwoPartySaveBarrier();
            var commandA = new CreateRideCommand(rider, Pickup(), Dropoff(), keyA);
            var commandB = new CreateRideCommand(rider, Pickup(), Dropoff(), keyB);

            var taskA = RunAsync(BarrierOptions(barrier), commandA);
            var taskB = RunAsync(BarrierOptions(barrier), commandB);
            var results = await Task.WhenAll(taskA, taskB);

            var successes = results.Where(r => r.IsSuccess).ToList();
            var failures = results.Where(r => !r.IsSuccess).ToList();
            var success = Assert.Single(successes);
            var failure = Assert.Single(failures);
            Assert.False(success.IsReplay);
            // Keys differ, so the loser must NOT become IDEMPOTENCY_CONFLICT.
            Assert.Equal(CreateRideError.ActiveTripConflict, failure.Error);

            await using var verify = new TripDbContext(PlainOptions());
            var trips = await verify.Trips.AsNoTracking()
                .Where(t => t.RiderId == rider).ToListAsync();
            var trip = Assert.Single(trips);
            Assert.Equal(success.TripId, trip.TripId);
            Assert.Equal(TripState.Created, trip.State);

            // Exactly one committed idempotency record among K1/K2: no orphan
            // record and no orphan Trip for the loser.
            var records = await verify.RequestIdempotency.AsNoTracking()
                .Where(r => r.Scope == scope).ToListAsync();
            var record = Assert.Single(records);
            Assert.Equal(success.TripId, record.ResourceId);
            Assert.Contains(record.Key, new[] { keyA, keyB });

            var loserKey = string.Equals(record.Key, keyA, StringComparison.Ordinal) ? keyB : keyA;
            Assert.Null(await verify.RequestIdempotency.AsNoTracking()
                .SingleOrDefaultAsync(r => r.Scope == scope && r.Key == loserKey));
        }
    }
}
