// Trip persistence round-trip proofs (A-W03-07) against real PostgreSQL.
//
// Proves the EF ComplexProperty mapping round-trips the actual W3 domain:
// null-assignment CREATED state, populated-assignment DRIVER_RESERVED state,
// FareSnapshot/locations/version coherence, and the assignment-coherence and
// state/assignment-coherence database defenses. Uses direct TripDbContext with the production model;
// no test-only model. Service boot itself is proven separately.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripPersistenceRoundTripTests
{
    private readonly TripPostgresFixture _fixture;

    public TripPersistenceRoundTripTests(TripPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private DbContextOptions<TripDbContext> Options() =>
        new DbContextOptionsBuilder<TripDbContext>()
            .UseNpgsql(_fixture.ConnectionString).Options;

    [Fact]
    public async Task Duplicate_ReservationId_Across_Trips_Is_Rejected()
    {
        // DSI-001 Trip-side defense (ux_trips_reservation_id): one logical
        // Driver reservation effect is current for at most one Trip. A second
        // trips row claiming the same reservation must fail unique violation
        // (23505), never commit a double claim.
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        var sharedReservation = Guid.NewGuid();
        Trip ReservedTrip(Guid tripId) => Trip.Reconstitute(
            tripId, Guid.NewGuid(),
            new TripLocation(10.762622, 106.660172),
            new TripLocation(10.776889, 106.700806),
            new FareSnapshot(
                "VND", 75000m, "p0-test",
                new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero)),
            TripState.DriverReserved,
            new TripAssignment(Guid.NewGuid(), sharedReservation, 1),
            new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero),
            3);

        await using (var db = new TripDbContext(options))
        {
            db.Trips.Add(ReservedTrip(Guid.NewGuid()));
            await db.SaveChangesAsync();
        }

        await using (var db = new TripDbContext(options))
        {
            db.Trips.Add(ReservedTrip(Guid.NewGuid()));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("23505", (ex.InnerException as PostgresException)?.SqlState);
        }
    }

    private static Trip NewCreatedTrip(Guid tripId)
    {
        var pickup = new TripLocation(10.762622, 106.660172);
        var dropoff = new TripLocation(10.776889, 106.700806);
        var fare = new FareSnapshot(
            "VND", 50000m, "p0-test",
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        return Trip.Create(
            tripId,
            Guid.NewGuid(),
            pickup,
            dropoff,
            fare,
            new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Created_RoundTrip_PreservesValues_WithNullAssignment()
    {
        var tripId = Guid.NewGuid();
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        Trip created;
        await using (var db = new TripDbContext(options))
        {
            created = NewCreatedTrip(tripId);
            db.Trips.Add(created);
            await db.SaveChangesAsync();
        }

        await using (var db = new TripDbContext(options))
        {
            var reloaded = await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);

            Assert.Equal(created.TripId, reloaded.TripId);
            Assert.Equal(created.RiderId, reloaded.RiderId);
            Assert.Equal(TripState.Created, reloaded.State);
            Assert.Equal(created.Version, reloaded.Version);
            Assert.Equal(created.Pickup, reloaded.Pickup);
            Assert.Equal(created.Dropoff, reloaded.Dropoff);
            Assert.Equal(created.Fare, reloaded.Fare);
            Assert.Null(reloaded.Assignment);
            // PostgreSQL timestamptz stores microsecond precision while .NET
            // ticks are 100ns: allow sub-millisecond truncation on CreatedAt.
            Assert.True(
                Math.Abs((created.CreatedAt.ToUniversalTime() - reloaded.CreatedAt.ToUniversalTime()).TotalMilliseconds) < 1,
                $"CreatedAt drifted more than 1ms: {created.CreatedAt:o} vs {reloaded.CreatedAt:o}");
        }
    }

    [Fact]
    public async Task AssignedState_RoundTrip_PreservesAssignmentAndVersion()
    {
        var tripId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        await using (var db = new TripDbContext(options))
        {
            var pickup = new TripLocation(10.762622, 106.660172);
            var dropoff = new TripLocation(10.776889, 106.700806);
            var fare = new FareSnapshot(
                "VND", 75000m, "p0-test",
                new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
            var trip = Trip.Reconstitute(
                tripId, Guid.NewGuid(), pickup, dropoff, fare,
                TripState.DriverReserved,
                new TripAssignment(driverId, reservationId, 1),
                new DateTimeOffset(2026, 9, 26, 7, 0, 0, TimeSpan.Zero),
                3);
            db.Trips.Add(trip);
            await db.SaveChangesAsync();
        }

        await using (var db = new TripDbContext(options))
        {
            var reloaded = await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);

            Assert.Equal(TripState.DriverReserved, reloaded.State);
            Assert.Equal(3, reloaded.Version);
            Assert.NotNull(reloaded.Assignment);
            Assert.Equal(driverId, reloaded.Assignment.DriverId);
            Assert.Equal(reservationId, reloaded.Assignment.ReservationId);
            Assert.Equal(1, reloaded.Assignment.MatchingRound);
            Assert.Equal(new TripLocation(10.762622, 106.660172), reloaded.Pickup);
            Assert.Equal(new TripLocation(10.776889, 106.700806), reloaded.Dropoff);
            Assert.Equal("VND", reloaded.Fare.Currency);
            Assert.Equal(75000m, reloaded.Fare.Amount);
        }
    }

    [Fact]
    public async Task AssignmentCoherenceCheck_RejectsPartialAssignmentRow()
    {
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        var tripId = Guid.NewGuid();
        await using var db = new TripDbContext(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
            "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
            "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
            "assigned_driver_id, reservation_id, matching_round) " +
            "VALUES ($1, $2, 'CREATED', now(), 0, 10.0, 106.0, 10.1, 106.1, " +
            "'VND', 50000, 'p0-test', now(), $3, NULL, NULL)";
        command.Parameters.Add(new NpgsqlParameter { Value = tripId });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });

        var rejected = await Assert.ThrowsAnyAsync<PostgresException>(async () =>
        {
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("23514", rejected.SqlState);
        Assert.Equal("CK_trips_assignment_coherence", rejected.ConstraintName);
    }

    [Fact]
    public async Task StateAssignmentCheck_RejectsDriverReservedWithoutAssignment()
    {
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        await using var db = new TripDbContext(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
            "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
            "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
            "assigned_driver_id, reservation_id, matching_round) " +
            "VALUES ($1, $2, 'DRIVER_RESERVED', now(), 0, 10.0, 106.0, 10.1, 106.1, " +
            "'VND', 50000, 'p0-test', now(), NULL, NULL, NULL)";
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });

        var rejected = await Assert.ThrowsAnyAsync<PostgresException>(async () =>
        {
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("23514", rejected.SqlState);
        Assert.Equal("CK_trips_state_assignment_coherence", rejected.ConstraintName);
    }

    [Fact]
    public async Task StateAssignmentCheck_RejectsMatchingWithFullAssignment()
    {
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        await using var db = new TripDbContext(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
            "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
            "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
            "assigned_driver_id, reservation_id, matching_round) " +
            "VALUES ($1, $2, 'MATCHING', now(), 0, 10.0, 106.0, 10.1, 106.1, " +
            "'VND', 50000, 'p0-test', now(), $3, $4, 1)";
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });

        var rejected = await Assert.ThrowsAnyAsync<PostgresException>(async () =>
        {
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("23514", rejected.SqlState);
        Assert.Equal("CK_trips_state_assignment_coherence", rejected.ConstraintName);
    }

    [Fact]
    public async Task StateAssignmentCheck_AllowsMatchingWithoutAssignment()
    {
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        var tripId = Guid.NewGuid();
        await using var db = new TripDbContext(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
            "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
            "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
            "assigned_driver_id, reservation_id, matching_round) " +
            "VALUES ($1, $2, 'MATCHING', now(), 0, 10.0, 106.0, 10.1, 106.1, " +
            "'VND', 50000, 'p0-test', now(), NULL, NULL, NULL)";
        command.Parameters.Add(new NpgsqlParameter { Value = tripId });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });

        await command.ExecuteNonQueryAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT state FROM trips WHERE trip_id = $1";
        verify.Parameters.Add(new NpgsqlParameter { Value = tripId });
        Assert.Equal("MATCHING", await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task StateAssignmentCheck_AllowsCancelledWithAndWithoutAssignment()
    {
        var options = Options();
        await using (var setup = new TripDbContext(options))
        {
            await setup.Database.MigrateAsync();
        }

        var bareId = Guid.NewGuid();
        var assignedId = Guid.NewGuid();
        await using var db = new TripDbContext(options);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using (var bare = connection.CreateCommand())
        {
            bare.CommandText =
                "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
                "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
                "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
                "assigned_driver_id, reservation_id, matching_round) " +
                "VALUES ($1, $2, 'CANCELLED', now(), 0, 10.0, 106.0, 10.1, 106.1, " +
                "'VND', 50000, 'p0-test', now(), NULL, NULL, NULL)";
            bare.Parameters.Add(new NpgsqlParameter { Value = bareId });
            bare.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            await bare.ExecuteNonQueryAsync();
        }

        await using (var assigned = connection.CreateCommand())
        {
            assigned.CommandText =
                "INSERT INTO trips (trip_id, rider_id, state, created_at, version, " +
                "pickup_latitude, pickup_longitude, dropoff_latitude, dropoff_longitude, " +
                "fare_currency, fare_amount, fare_policy_version, fare_created_at, " +
                "assigned_driver_id, reservation_id, matching_round) " +
                "VALUES ($1, $2, 'CANCELLED', now(), 1, 10.0, 106.0, 10.1, 106.1, " +
                "'VND', 50000, 'p0-test', now(), $3, $4, 2)";
            assigned.Parameters.Add(new NpgsqlParameter { Value = assignedId });
            assigned.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            assigned.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            assigned.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            await assigned.ExecuteNonQueryAsync();
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT count(*) FROM trips WHERE trip_id = $1 OR trip_id = $2";
        verify.Parameters.Add(new NpgsqlParameter { Value = bareId });
        verify.Parameters.Add(new NpgsqlParameter { Value = assignedId });
        Assert.Equal(2L, await verify.ExecuteScalarAsync());
    }
}
