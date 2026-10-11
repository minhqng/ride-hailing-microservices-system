// CreateRide W4 sequential proofs against disposable real PostgreSQL 18.x.
//
// Covers A-W04-01/02/03/04/05 at the handler level: FUN-04 sequential
// idempotency (same K/X replays, same K/Y conflicts), response-loss replay,
// replay precedence over the active-Trip rule, sequential active-Trip
// conflict, terminal-frees-Rider, cross-Rider key isolation, key validation,
// FareSnapshot stability and the direct partial-unique-index defense
// (DSI-013). Each test uses fresh Rider identities so the shared disposable
// database cannot leak state between tests. Concurrent race proofs belong to
// the next W4 slice.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.TripService.Application.Trips.CreateRide;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class CreateRideComponentTests
{
    private readonly TripPostgresFixture _fixture;

    public CreateRideComponentTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private DbContextOptions<TripDbContext> Options() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private CreateRideHandler Handler(TripDbContext db) => new(db, new FixedTimeProvider(FixedNow));

    private static CreateRideCommand Command(Guid rider, string key, TripLocation? pickup = null, TripLocation? dropoff = null) =>
        new(rider, pickup ?? new TripLocation(10.762622, 106.660172), dropoff ?? new TripLocation(10.776889, 106.700806), key);

    private async Task MigrateAsync()
    {
        var options = Options();
        await using var setup = new TripDbContext(options);
        await setup.Database.MigrateAsync();
    }

    private async Task<CreateRideResult> CreateAsync(Guid rider, string key, TripLocation? pickup = null, TripLocation? dropoff = null)
    {
        await using var db = new TripDbContext(Options());
        return await Handler(db).HandleAsync(Command(rider, key, pickup, dropoff));
    }

    [Fact]
    public async Task Create_NewRequest_PersistsTripFareAndIdempotencyAtomically()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        var result = await CreateAsync(rider, "k-new");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsReplay);
        Assert.NotEqual(Guid.Empty, result.TripId);

        var options = Options();
        await using var verify = new TripDbContext(options);
        var trip = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == result.TripId);
        Assert.Equal(rider, trip.RiderId);
        Assert.Equal(TripState.Created, trip.State);
        Assert.Equal("VND", trip.Fare.Currency);
        Assert.Equal("p0-w4-v1", trip.Fare.PricingPolicyVersion);
        Assert.Equal(DeterministicFareCalculator.Calculate(trip.Pickup, trip.Dropoff, FixedNow).Amount, trip.Fare.Amount);

        var scope = CreateRideRequestFingerprint.BuildScope(rider);
        var record = await verify.RequestIdempotency.AsNoTracking()
            .SingleAsync(r => r.Scope == scope && r.Key == "k-new");
        Assert.Equal(result.TripId, record.ResourceId);
        Assert.Equal(
            CreateRideRequestFingerprint.Compute(rider, trip.Pickup, trip.Dropoff),
            record.RequestFingerprint);

        // One TimeProvider timestamp feeds all three rows (PostgreSQL
        // timestamptz truncates sub-millisecond precision: allow < 1ms drift).
        foreach (var stamp in new[] { trip.CreatedAt, trip.Fare.CreatedAt, record.CreatedAt })
        {
            Assert.True(
                Math.Abs((stamp.ToUniversalTime() - FixedNow).TotalMilliseconds) < 1,
                $"Creation timestamp drifted: {stamp:o} vs {FixedNow:o}");
        }
    }

    [Fact]
    public async Task SequentialFun04_SameKeySameRequest_Replays_AndSameKeyDifferentRequest_Conflicts()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        var first = await CreateAsync(rider, "K");
        Assert.True(first.IsSuccess);
        Assert.False(first.IsReplay);
        var tripId = first.TripId;

        var replay = await CreateAsync(rider, "K");
        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(tripId, replay.TripId);

        var conflict = await CreateAsync(
            rider, "K",
            new TripLocation(10.762622, 106.660172),
            new TripLocation(11.0, 107.0));
        Assert.False(conflict.IsSuccess);
        Assert.Equal(CreateRideError.IdempotencyConflict, conflict.Error);

        var options = Options();
        await using var verify = new TripDbContext(options);
        var scope = CreateRideRequestFingerprint.BuildScope(rider);

        Assert.Equal(1, await verify.Trips.AsNoTracking().CountAsync(t => t.RiderId == rider));
        var records = await verify.RequestIdempotency.AsNoTracking()
            .Where(r => r.Scope == scope).ToListAsync();
        var single = Assert.Single(records);
        Assert.Equal("K", single.Key);
        Assert.Equal(tripId, single.ResourceId);
        Assert.Equal(
            CreateRideRequestFingerprint.Compute(rider, new TripLocation(10.762622, 106.660172), new TripLocation(10.776889, 106.700806)),
            single.RequestFingerprint);

        var fareBefore = (await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId)).Fare;
        var replayAgain = await CreateAsync(rider, "K");
        Assert.True(replayAgain.IsSuccess);
        var fareAfter = await verify.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
        Assert.Equal(fareBefore, fareAfter.Fare);
    }

    [Fact]
    public async Task ResponseLoss_DiscardedResult_ReplaysSameTrip_WithOneRowEach()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        // Successful create whose response is lost.
        _ = await CreateAsync(rider, "response-loss");

        var retry = await CreateAsync(rider, "response-loss");
        Assert.True(retry.IsSuccess);
        Assert.True(retry.IsReplay);

        var options = Options();
        await using var verify = new TripDbContext(options);
        var scope = CreateRideRequestFingerprint.BuildScope(rider);
        Assert.Equal(1, await verify.Trips.AsNoTracking().CountAsync(t => t.RiderId == rider));
        Assert.Equal(1, await verify.RequestIdempotency.AsNoTracking().CountAsync(r => r.Scope == scope));
    }

    [Fact]
    public async Task Replay_PrecedesActiveCheck_WhileReferencedTripIsActive()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        var first = await CreateAsync(rider, "active-replay");
        Assert.True(first.IsSuccess);

        // The referenced Trip is CREATED, which IS an active DSI-013 state: a
        // handler that checked active-before-replay would wrongly conflict.
        var replay = await CreateAsync(rider, "active-replay");
        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.TripId, replay.TripId);

        var options = Options();
        await using var verify = new TripDbContext(options);
        Assert.Equal(1, await verify.Trips.AsNoTracking().CountAsync(t => t.RiderId == rider));
    }

    [Fact]
    public async Task NewKey_WhileActiveTripExists_ReturnsActiveConflict_WithNoSideEffects()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        var first = await CreateAsync(rider, "K1");
        Assert.True(first.IsSuccess);

        var second = await CreateAsync(
            rider, "K2",
            new TripLocation(10.762622, 106.660172),
            new TripLocation(11.0, 107.0));
        Assert.False(second.IsSuccess);
        Assert.Equal(CreateRideError.ActiveTripConflict, second.Error);

        var options = Options();
        await using var verify = new TripDbContext(options);
        Assert.Equal(1, await verify.Trips.AsNoTracking().CountAsync(t => t.RiderId == rider));
        var scope = CreateRideRequestFingerprint.BuildScope(rider);
        Assert.Equal(1, await verify.RequestIdempotency.AsNoTracking().CountAsync(r => r.Scope == scope));
        Assert.Null(await verify.RequestIdempotency.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Scope == scope && r.Key == "K2"));
    }

    [Fact]
    public async Task TerminalTrip_FreesRider_ForNewCreation()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();
        var options = Options();

        await using (var seed = new TripDbContext(options))
        {
            var terminal = Trip.Reconstitute(
                Guid.NewGuid(), rider,
                new TripLocation(10.762622, 106.660172),
                new TripLocation(10.776889, 106.700806),
                new FareSnapshot("VND", 33568.45m, "p0-w4-v1", FixedNow),
                TripState.Cancelled, 1, null, FixedNow, FixedNow, 2);
            seed.Trips.Add(terminal);
            await seed.SaveChangesAsync();
        }

        var result = await CreateAsync(rider, "after-terminal");
        Assert.True(result.IsSuccess);
        Assert.False(result.IsReplay);

        await using var verify = new TripDbContext(options);
        Assert.Equal(2, await verify.Trips.AsNoTracking().CountAsync(t => t.RiderId == rider));
    }

    [Fact]
    public async Task SameKey_DifferentRiders_AreIsolatedNamespaces()
    {
        await MigrateAsync();
        var riderA = Guid.NewGuid();
        var riderB = Guid.NewGuid();

        var a = await CreateAsync(riderA, "shared-key");
        var b = await CreateAsync(riderB, "shared-key");

        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);
        Assert.NotEqual(a.TripId, b.TripId);

        // Each Rider replays only their own Trip: no cross-Rider disclosure.
        var replayA = await CreateAsync(riderA, "shared-key");
        var replayB = await CreateAsync(riderB, "shared-key");
        Assert.True(replayA.IsReplay);
        Assert.True(replayB.IsReplay);
        Assert.Equal(a.TripId, replayA.TripId);
        Assert.Equal(b.TripId, replayB.TripId);

        var options = Options();
        await using var verify = new TripDbContext(options);
        var scopeA = CreateRideRequestFingerprint.BuildScope(riderA);
        var scopeB = CreateRideRequestFingerprint.BuildScope(riderB);
        var recordA = await verify.RequestIdempotency.AsNoTracking()
            .SingleAsync(r => r.Scope == scopeA && r.Key == "shared-key");
        var recordB = await verify.RequestIdempotency.AsNoTracking()
            .SingleAsync(r => r.Scope == scopeB && r.Key == "shared-key");
        Assert.Equal(a.TripId, recordA.ResourceId);
        Assert.Equal(b.TripId, recordB.ResourceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankKey_IsValidationFailure(string key)
    {
        await MigrateAsync();

        var result = await CreateAsync(Guid.NewGuid(), key);

        Assert.False(result.IsSuccess);
        Assert.Equal(CreateRideError.Validation, result.Error);
    }

    [Fact]
    public async Task OversizedKey_IsValidationFailure()
    {
        await MigrateAsync();

        var result = await CreateAsync(Guid.NewGuid(), new string('k', 129));

        Assert.False(result.IsSuccess);
        Assert.Equal(CreateRideError.Validation, result.Error);
    }

    [Fact]
    public async Task Keys_AreOpaqueExactIdentities_CaseSensitive()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        var lower = await CreateAsync(rider, "abc");
        Assert.True(lower.IsSuccess);

        // "ABC" must NOT replay "abc": had the handler normalized case it
        // would return IsReplay; instead the fresh key reaches the
        // active-Trip rule for the same Rider.
        var upper = await CreateAsync(rider, "ABC");
        Assert.False(upper.IsSuccess);
        Assert.Equal(CreateRideError.ActiveTripConflict, upper.Error);
    }

    [Fact]
    public async Task FareSnapshot_IsStableAcrossReplay()
    {
        await MigrateAsync();
        var rider = Guid.NewGuid();

        var first = await CreateAsync(rider, "fare-stable");
        Assert.True(first.IsSuccess);

        var options = Options();
        FareSnapshot before;
        await using (var db = new TripDbContext(options))
        {
            before = (await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == first.TripId)).Fare;
        }

        var replay = await CreateAsync(rider, "fare-stable");
        Assert.True(replay.IsReplay);

        await using (var db = new TripDbContext(options))
        {
            var after = (await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == first.TripId)).Fare;
            Assert.Equal(before.Amount, after.Amount);
            Assert.Equal(before.Currency, after.Currency);
            Assert.Equal(before.PricingPolicyVersion, after.PricingPolicyVersion);
            Assert.Equal(before.CreatedAt, after.CreatedAt);
        }
    }

    [Fact]
    public async Task ActiveRiderIndex_RejectsSecondActive_AllowsTerminalCombinations()
    {
        await MigrateAsync();
        var options = Options();
        var riderActive = Guid.NewGuid();
        var riderMixed = Guid.NewGuid();
        var riderTerminal = Guid.NewGuid();

        await using var db = new TripDbContext(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        static async Task InsertTripAsync(NpgsqlConnection connection, Guid tripId, Guid rider, string state)
        {
            await using var cmd = connection.CreateCommand();
            // W5 round durability: post-CREATED non-cancelled states carry an
            // open round, so the helper mirrors the domain invariant
            // (MATCHING/MATCH_FAILED rows carry round 1, the rest carry 0).
            var round = state is "MATCHING" or "MATCH_FAILED" ? 1 : 0;
            cmd.CommandText =
                "INSERT INTO trips (trip_id, rider_id, state, created_at, updated_at, current_matching_round, version, " +
                "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
                "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
                "assigned_driver_id, reservation_id, matching_round) " +
                "VALUES ($1, $2, $3, now(), now(), $4, 0, 10.0, 106.0, 10.1, 106.1, " +
                "'VND', 50000, 'p0-w4-v1', now(), NULL, NULL, NULL)";
            cmd.Parameters.Add(new NpgsqlParameter { Value = tripId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = rider });
            cmd.Parameters.Add(new NpgsqlParameter { Value = state });
            cmd.Parameters.Add(new NpgsqlParameter { Value = round });
            await cmd.ExecuteNonQueryAsync();
        }

        // A. Two active-state rows for the same Rider: second is rejected.
        await InsertTripAsync(connection, Guid.NewGuid(), riderActive, "CREATED");
        var duplicate = await Assert.ThrowsAnyAsync<PostgresException>(() =>
            InsertTripAsync(connection, Guid.NewGuid(), riderActive, "MATCHING"));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("UX_trips_one_active_per_rider", duplicate.ConstraintName);

        // B. Terminal row + active row for the same Rider: allowed.
        await InsertTripAsync(connection, Guid.NewGuid(), riderMixed, "CANCELLED");
        await InsertTripAsync(connection, Guid.NewGuid(), riderMixed, "CREATED");

        // C. Two terminal rows for the same Rider: allowed.
        await InsertTripAsync(connection, Guid.NewGuid(), riderTerminal, "CANCELLED");
        await InsertTripAsync(connection, Guid.NewGuid(), riderTerminal, "MATCH_FAILED");
    }

    [Fact]
    public void ModelActiveIndex_PredicateMatchesDomainPolicy()
    {
        var options = new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new TripDbContext(options);

        var index = Assert.Single(
            db.Model.FindEntityType(typeof(Trip))!.GetIndexes(),
            i => i.GetDatabaseName() == "UX_trips_one_active_per_rider");
        Assert.True(index.IsUnique);
        var filter = index.GetFilter();
        Assert.NotNull(filter);

        foreach (var state in TripStatePolicy.ActiveForRiderBooking)
        {
            Assert.Contains($"'{state.ToCanonicalName()}'", filter, StringComparison.Ordinal);
        }

        foreach (var terminal in new[] { TripState.Completed, TripState.Cancelled, TripState.MatchFailed })
        {
            Assert.DoesNotContain($"'{terminal.ToCanonicalName()}'", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MaxLengthKey_IsAccepted_AtExactly128Chars()
    {
        await MigrateAsync();

        var result = await CreateAsync(Guid.NewGuid(), new string('k', 128));

        Assert.True(result.IsSuccess);
        Assert.False(result.IsReplay);
    }

    [Fact]
    public async Task Replay_AfterTerminal_ReplaysTerminalTripId()
    {
        await MigrateAsync();
        var options = Options();
        var rider = Guid.NewGuid();
        const string key = "terminal-replay";

        var first = await CreateAsync(rider, key);
        Assert.True(first.IsSuccess);

        // Move the referenced Trip to a terminal state out-of-band (a later
        // workflow owns transitions; here only the replay binding is proven).
        await using (var db = new TripDbContext(options))
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE trips SET state = 'CANCELLED' WHERE trip_id = $1";
            cmd.Parameters.Add(new NpgsqlParameter { Value = first.TripId });
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        // Same key + same semantic request still replays the SAME (now
        // terminal) Trip: the key stays bound to its logical creation.
        var replay = await CreateAsync(rider, key);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.TripId, replay.TripId);

        // A fresh key for the freed Rider creates anew.
        var fresh = await CreateAsync(rider, "after-terminal");
        Assert.True(fresh.IsSuccess);
        Assert.False(fresh.IsReplay);
        Assert.NotEqual(first.TripId, fresh.TripId);
    }
}
