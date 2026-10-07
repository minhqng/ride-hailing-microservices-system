// B-W03-07: Npgsql raw repository (chốt ③). Plain upsert/load; no OCC yet (W6).
using Npgsql;
using RideHailing.DriverService.Domain.Drivers;

namespace RideHailing.DriverService.Infrastructure.Persistence;

public sealed class DriverRepository
{
    private readonly string _connectionString;

    public DriverRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task EnsureTablesAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(DriverSqlBaseline.Load(), conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// TEST/SEED-ONLY blind upsert (round-trips, fixture seeding). Production
    /// HTTP mutations MUST use the guarded Try* methods: a blind full-row
    /// overwrite with a stale object silently unreserves newer state
    /// (DSI-003 lost-reservation shape). Kept public for test seeding.
    /// </summary>
    public async Task SaveAsync(Driver driver, CancellationToken ct = default)
    {
        const string Sql = """
            INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, updated_at)
            VALUES (@id, @user, @status, @version, @reservedTrip, @reservation, @activeTrip, @vehicle, @updated)
            ON CONFLICT (driver_id) DO UPDATE SET
                status = EXCLUDED.status,
                version = EXCLUDED.version,
                reserved_trip_id = EXCLUDED.reserved_trip_id,
                reservation_id = EXCLUDED.reservation_id,
                active_trip_id = EXCLUDED.active_trip_id,
                vehicle_id = EXCLUDED.vehicle_id,
                updated_at = EXCLUDED.updated_at;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("id", driver.DriverId);
        cmd.Parameters.AddWithValue("user", driver.UserId);
        cmd.Parameters.AddWithValue("status", ToDb(driver.Status));
        cmd.Parameters.AddWithValue("version", driver.Version);
        cmd.Parameters.AddWithValue("reservedTrip", (object?)driver.ReservedTripId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reservation", (object?)driver.ReservationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("activeTrip", (object?)driver.ActiveTripId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vehicle", (object?)driver.VehicleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("updated", driver.UpdatedAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Insert-only create path for POST /drivers. Concurrent duplicate
    /// creates linearize on the drivers primary key: exactly one insert
    /// wins, losers observe the unique violation instead of silently
    /// resetting existing state (DSR-002 duplicate-delivery safety).
    /// Returns false when a driver with the same id already exists.
    /// </summary>
    public async Task<bool> TryInsertAsync(Driver driver, CancellationToken ct = default)
    {
        const string Sql = """
            INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, updated_at)
            VALUES (@id, @user, @status, @version, @reservedTrip, @reservation, @activeTrip, @vehicle, @updated);
            """;
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(Sql, conn);
            cmd.Parameters.AddWithValue("id", driver.DriverId);
            cmd.Parameters.AddWithValue("user", driver.UserId);
            cmd.Parameters.AddWithValue("status", ToDb(driver.Status));
            cmd.Parameters.AddWithValue("version", driver.Version);
            cmd.Parameters.AddWithValue("reservedTrip", (object?)driver.ReservedTripId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("reservation", (object?)driver.ReservationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("activeTrip", (object?)driver.ActiveTripId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("vehicle", (object?)driver.VehicleId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("updated", driver.UpdatedAt.UtcDateTime);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<Driver?> LoadAsync(Guid driverId, CancellationToken ct = default)
    {
        const string Sql = """
            SELECT driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, created_at, updated_at
            FROM drivers WHERE driver_id = @id;
            """;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("id", driverId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return Driver.Rehydrate(
            reader.GetGuid(0), reader.GetGuid(1), FromDb(reader.GetString(2)), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
    }

    /// <summary>
    /// B-W06-01/02/03: OCC reserve (Doc 01 §1.4/§4.4, DSI-001/003/005, Doc 02 §1.4).
    /// One short local transaction, no network call inside: SELECT .. FOR UPDATE
    /// serializes concurrent contenders on the driver row (the linearization
    /// point is lock acquisition order), then the single domain rule
    /// (Driver.Reserve) decides on the FRESH row, so there is no check-then-act
    /// race. Same-reservation replay succeeds without a write; stale versions
    /// and foreign reservations conflict without mutating.
    /// A cross-row reservation_id collision (caller reused R on another driver)
    /// is backstopped by ux_drivers_reservation_id and reported as conflict.
    /// </summary>
    public async Task<ReserveResult> TryReserveAsync(
        Guid driverId, Guid tripId, Guid reservationId, int expectedVersion,
        CancellationToken ct = default)
    {
        if (driverId == Guid.Empty || tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new ArgumentException("driverId/tripId/reservationId must not be empty.");
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentException("expectedVersion must be >= 1.");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            const string SelectForUpdate = """
                SELECT driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, created_at, updated_at
                FROM drivers WHERE driver_id = @id FOR UPDATE;
                """;
            Driver? current;
            await using (var sel = new NpgsqlCommand(SelectForUpdate, conn, tx))
            {
                sel.Parameters.AddWithValue("id", driverId);
                await using var reader = await sel.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    current = null;
                }
                else
                {
                    current = Driver.Rehydrate(
                        reader.GetGuid(0), reader.GetGuid(1), FromDb(reader.GetString(2)), reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetGuid(4),
                        reader.IsDBNull(5) ? null : reader.GetGuid(5),
                        reader.IsDBNull(6) ? null : reader.GetGuid(6),
                        reader.IsDBNull(7) ? null : reader.GetGuid(7),
                        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
                }
            }

            if (current is null)
            {
                // Reader (and command) are disposed above: rollback never runs
                // while a command is still in progress on this connection.
                await tx.RollbackAsync(ct);
                return ReserveResult.NotFound();
            }

            var replayed = current.Status == DriverStatus.Reserved
                && current.ReservedTripId == tripId
                && current.ReservationId == reservationId;

            try
            {
                current.Reserve(tripId, reservationId, expectedVersion);
            }
            catch (DriverDomainException ex)
            {
                await tx.RollbackAsync(ct);
                return ReserveResult.Conflict(ex.Code, ex.Message);
            }

            const string Update = """
                UPDATE drivers SET
                    status = @status,
                    version = @version,
                    reserved_trip_id = @reservedTrip,
                    reservation_id = @reservation,
                    active_trip_id = @activeTrip,
                    vehicle_id = @vehicle,
                    updated_at = @updated
                WHERE driver_id = @id;
                """;
            await using (var upd = new NpgsqlCommand(Update, conn, tx))
            {
                upd.Parameters.AddWithValue("id", current.DriverId);
                upd.Parameters.AddWithValue("status", ToDb(current.Status));
                upd.Parameters.AddWithValue("version", current.Version);
                upd.Parameters.AddWithValue("reservedTrip", (object?)current.ReservedTripId ?? DBNull.Value);
                upd.Parameters.AddWithValue("reservation", (object?)current.ReservationId ?? DBNull.Value);
                upd.Parameters.AddWithValue("activeTrip", (object?)current.ActiveTripId ?? DBNull.Value);
                upd.Parameters.AddWithValue("vehicle", (object?)current.VehicleId ?? DBNull.Value);
                upd.Parameters.AddWithValue("updated", current.UpdatedAt.UtcDateTime);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return replayed ? ReserveResult.Replayed(current) : ReserveResult.Won(current);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Best-effort: the failed UPDATE may still be active on this
            // connection, and the connector may already be broken. Either way
            // the single statement failed atomically (our row is unmodified)
            // and tx disposal rolls back what remains.
            try
            {
                await tx.RollbackAsync(ct);
            }
            catch (NpgsqlException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return ReserveResult.Conflict(
                DriverErrors.DriverNotAvailable,
                "Reservation identity is already held by another driver.");
        }
    }

    /// <summary>
    /// B-W07-01/02: exact-token release under the same row-lock discipline as
    /// reserve (DSI-004 fencing, DSI-033 exact identity, R-06). The domain
    /// decides on the fresh locked row: exact token frees (effect), coherent
    /// AVAILABLE is an idempotent cleanup no-op, a live foreign reservation
    /// conflicts without mutation. No version parameter: token equality on
    /// the locked row is the complete guard.
    /// </summary>
    public async Task<DriverMutationResult> TryReleaseAsync(
        Guid driverId, Guid tripId, Guid reservationId,
        CancellationToken ct = default)
    {
        if (driverId == Guid.Empty || tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new ArgumentException("driverId/tripId/reservationId must not be empty.");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await LoadForUpdateAsync(conn, tx, driverId, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return DriverMutationResult.NotFound();
        }

        var effected = current.Status == DriverStatus.Reserved
            && current.ReservedTripId == tripId
            && current.ReservationId == reservationId;

        try
        {
            current.Release(tripId, reservationId);
        }
        catch (DriverDomainException ex)
        {
            await tx.RollbackAsync(ct);
            return DriverMutationResult.Conflict(ex.Code, ex.Message);
        }

        await StoreAsync(conn, tx, current, ct);
        await tx.CommitAsync(ct);
        return effected
            ? DriverMutationResult.Applied(current)
            : DriverMutationResult.Unchanged(current);
    }

    /// <summary>
    /// B-W07-04 Race B: version-fenced GoOffline under row lock (DSR-004).
    /// Only one of offline-vs-reserve may commit from the same version; the
    /// loser observes the winner's row and conflicts.
    /// </summary>
    public async Task<DriverMutationResult> TryGoOfflineAsync(
        Guid driverId, int expectedVersion,
        CancellationToken ct = default)
    {
        if (driverId == Guid.Empty)
        {
            throw new ArgumentException("driverId must not be empty.");
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentException("expectedVersion must be >= 1.");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await LoadForUpdateAsync(conn, tx, driverId, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return DriverMutationResult.NotFound();
        }

        var already = current.Status == DriverStatus.Offline;
        try
        {
            current.GoOffline(expectedVersion);
        }
        catch (DriverDomainException ex)
        {
            await tx.RollbackAsync(ct);
            return DriverMutationResult.Conflict(ex.Code, ex.Message);
        }

        await StoreAsync(conn, tx, current, ct);
        await tx.CommitAsync(ct);
        return already
            ? DriverMutationResult.Unchanged(current)
            : DriverMutationResult.Applied(current);
    }

    /// <summary>
    /// DSI-003 closure for the online path: version-fenced GoOnline under row
    /// lock. A stale online object must not overwrite a newer RESERVED row
    /// (the same lost-reservation shape as Race B, via the blind upsert).
    /// </summary>
    public async Task<DriverMutationResult> TryGoOnlineAsync(
        Guid driverId, int expectedVersion,
        CancellationToken ct = default)
    {
        if (driverId == Guid.Empty)
        {
            throw new ArgumentException("driverId must not be empty.");
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentException("expectedVersion must be >= 1.");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = await LoadForUpdateAsync(conn, tx, driverId, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return DriverMutationResult.NotFound();
        }

        var already = current.Status == DriverStatus.Available;
        try
        {
            current.GoOnline(expectedVersion);
        }
        catch (DriverDomainException ex)
        {
            await tx.RollbackAsync(ct);
            return DriverMutationResult.Conflict(ex.Code, ex.Message);
        }

        await StoreAsync(conn, tx, current, ct);
        await tx.CommitAsync(ct);
        return already
            ? DriverMutationResult.Unchanged(current)
            : DriverMutationResult.Applied(current);
    }

    /// <summary>
    /// B-W07-07: read-only reservation truth for reconciliation (Doc 02 §12.6).
    /// Plain SELECT, no lock: this is a truth query, not a mutation. A racing
    /// writer only means "current truth", which is exactly what polling needs.
    /// </summary>
    public async Task<ReservationQuery> QueryReservationAsync(
        Guid driverId, Guid reservationId,
        CancellationToken ct = default)
    {
        if (driverId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new ArgumentException("driverId/reservationId must not be empty.");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        const string Sql = """
            SELECT status, version, reserved_trip_id, reservation_id
            FROM drivers WHERE driver_id = @id;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("id", driverId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new ReservationQuery(ReservationQueryOutcome.NotFound, null);
        }

        var status = FromDb(reader.GetString(0));
        var version = reader.GetInt32(1);
        Guid? reservedTrip = reader.IsDBNull(2) ? null : reader.GetGuid(2);
        Guid? held = reader.IsDBNull(3) ? null : reader.GetGuid(3);

        ReservationQueryOutcome outcome = status switch
        {
            DriverStatus.Reserved when held == reservationId => ReservationQueryOutcome.OwnedByThisReservation,
            DriverStatus.Reserved => ReservationQueryOutcome.ReservedByOther,
            DriverStatus.Available => ReservationQueryOutcome.NotCommittedOrAvailable,
            DriverStatus.Offline => ReservationQueryOutcome.Offline,
            DriverStatus.OnTrip => ReservationQueryOutcome.OnTrip,
            _ => throw new DriverDomainException(
                DriverErrors.IllegalTransition, $"Unknown DB status '{status}'."),
        };
        return new ReservationQuery(outcome, version);
    }

    private static async Task<Driver?> LoadForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid driverId, CancellationToken ct)
    {
        const string Sql = """
            SELECT driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, created_at, updated_at
            FROM drivers WHERE driver_id = @id FOR UPDATE;
            """;
        await using var sel = new NpgsqlCommand(Sql, conn, tx);
        sel.Parameters.AddWithValue("id", driverId);
        await using var reader = await sel.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return Driver.Rehydrate(
            reader.GetGuid(0), reader.GetGuid(1), FromDb(reader.GetString(2)), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
    }

    private static async Task StoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Driver driver, CancellationToken ct)
    {
        const string Sql = """
            UPDATE drivers SET
                status = @status,
                version = @version,
                reserved_trip_id = @reservedTrip,
                reservation_id = @reservation,
                active_trip_id = @activeTrip,
                vehicle_id = @vehicle,
                updated_at = @updated
            WHERE driver_id = @id;
            """;
        await using var upd = new NpgsqlCommand(Sql, conn, tx);
        upd.Parameters.AddWithValue("id", driver.DriverId);
        upd.Parameters.AddWithValue("status", ToDb(driver.Status));
        upd.Parameters.AddWithValue("version", driver.Version);
        upd.Parameters.AddWithValue("reservedTrip", (object?)driver.ReservedTripId ?? DBNull.Value);
        upd.Parameters.AddWithValue("reservation", (object?)driver.ReservationId ?? DBNull.Value);
        upd.Parameters.AddWithValue("activeTrip", (object?)driver.ActiveTripId ?? DBNull.Value);
        upd.Parameters.AddWithValue("vehicle", (object?)driver.VehicleId ?? DBNull.Value);
        upd.Parameters.AddWithValue("updated", driver.UpdatedAt.UtcDateTime);
        await upd.ExecuteNonQueryAsync(ct);
    }

    public static string ToDb(DriverStatus status) => status switch
    {
        DriverStatus.Offline => "OFFLINE",
        DriverStatus.Available => "AVAILABLE",
        DriverStatus.Reserved => "RESERVED",
        DriverStatus.OnTrip => "ON_TRIP",
        _ => throw new DriverDomainException(DriverErrors.IllegalTransition, $"Unknown status {status}."),
    };

    public static DriverStatus FromDb(string status) => status switch
    {
        "OFFLINE" => DriverStatus.Offline,
        "AVAILABLE" => DriverStatus.Available,
        "RESERVED" => DriverStatus.Reserved,
        "ON_TRIP" => DriverStatus.OnTrip,
        _ => throw new DriverDomainException(DriverErrors.IllegalTransition, $"Unknown DB status '{status}'."),
    };
}
