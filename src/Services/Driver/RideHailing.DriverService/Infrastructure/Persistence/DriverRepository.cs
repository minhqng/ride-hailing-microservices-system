// B-W03-07: Npgsql raw repository (chốt ③). Plain upsert/load; no OCC yet (W6).
// B-W09-02: every authoritative Try* mutation records its required Driver
// event through C's shared Outbox store on the SAME connection/transaction.
using System.Diagnostics;
using Npgsql;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Outbox;
using RideHailing.DriverService.Infrastructure.Telemetry;
using RideHailing.Messaging.Outbox;

namespace RideHailing.DriverService.Infrastructure.Persistence;

public sealed partial class DriverRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly DriverOutboxWriter _outbox;
    private readonly DriverMetrics? _metrics;

    public DriverRepository(
        NpgsqlDataSource dataSource, NpgsqlOutboxStore outboxStore, DriverMetrics? metrics = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _outbox = new DriverOutboxWriter(outboxStore);
        _metrics = metrics;
    }

    public async Task EnsureTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        // AUD-10: replicas serialize the baseline on a stable advisory key,
        // matching Payment's migration coordination. Transactional DDL rolls
        // back on a crash and releases the lock with the transaction.
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(754302)", conn, transaction))
        {
            await gate.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(DriverSqlBaseline.Load(), conn, transaction);
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
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
            INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, updated_at, reserved_round)
            VALUES (@id, @user, @status, @version, @reservedTrip, @reservation, @activeTrip, @vehicle, @updated, @round)
            ON CONFLICT (driver_id) DO UPDATE SET
                status = EXCLUDED.status,
                version = EXCLUDED.version,
                reserved_trip_id = EXCLUDED.reserved_trip_id,
                reservation_id = EXCLUDED.reservation_id,
                active_trip_id = EXCLUDED.active_trip_id,
                vehicle_id = EXCLUDED.vehicle_id,
                updated_at = EXCLUDED.updated_at,
                reserved_round = EXCLUDED.reserved_round;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
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
        cmd.Parameters.AddWithValue("round", (object?)driver.ReservationRound ?? DBNull.Value);
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
            INSERT INTO drivers (driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, updated_at, reserved_round)
            VALUES (@id, @user, @status, @version, @reservedTrip, @reservation, @activeTrip, @vehicle, @updated, @round);
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
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
            cmd.Parameters.AddWithValue("round", (object?)driver.ReservationRound ?? DBNull.Value);
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
            SELECT driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, created_at, updated_at, reserved_round
            FROM drivers WHERE driver_id = @id;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("id", driverId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return RehydrateFromReader(reader);
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
    /// matchingRound (W08 hardening): provenance for DriverReservedV1 — recorded
    /// on the win so restart/replay publication carries the SAME generation.
    ///
    /// B-W12-01/04: instrumented entry. The success counter is emitted only
    /// after the core returns, which is after COMMIT; conflicts are counted
    /// only when the domain rule or the reservation-identity backstop rejected
    /// them. Database/transport exceptions propagate without a domain metric.
    /// </summary>
    public async Task<ReserveResult> TryReserveAsync(
        Guid driverId, Guid tripId, Guid reservationId, int expectedVersion,
        int? matchingRound = null,
        CancellationToken ct = default)
    {
        using var activity = DriverTelemetry.Activities.StartActivity("driver.reserve", ActivityKind.Internal);
        activity?.SetTag("trip.id", tripId);
        activity?.SetTag("reservation.id", reservationId);
        if (matchingRound.HasValue)
        {
            activity?.SetTag("matching.round", matchingRound.Value);
        }

        try
        {
            var result = await TryReserveCoreAsync(
                driverId, tripId, reservationId, expectedVersion, matchingRound, ct);

            var outcome = result.Outcome switch
            {
                ReserveOutcome.Won => "won",
                ReserveOutcome.Replayed => "replayed",
                ReserveOutcome.Conflict => "conflict",
                ReserveOutcome.DriverNotFound => "driver_not_found",
                _ => "unknown",
            };
            activity?.SetTag("driver.reserve.outcome", outcome);

            // Only a committed NEW reservation is a success effect. A
            // same-reservation replay already held the effect and must not
            // inflate the success counter.
            if (result.Outcome == ReserveOutcome.Won)
            {
                _metrics?.ReserveSucceeded();
            }
            else if (result.Outcome == ReserveOutcome.Conflict)
            {
                _metrics?.ReserveConflict(DriverMetrics.ReserveReasonClass(result.ConflictCode));
            }
            else if (result.Outcome == ReserveOutcome.DriverNotFound)
            {
                _metrics?.ReserveConflict("driver_not_found");
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }

    private async Task<ReserveResult> TryReserveCoreAsync(
        Guid driverId, Guid tripId, Guid reservationId, int expectedVersion,
        int? matchingRound,
        CancellationToken ct)
    {
        if (driverId == Guid.Empty || tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new ArgumentException("driverId/tripId/reservationId must not be empty.");
        }

        if (expectedVersion < 1)
        {
            throw new ArgumentException("expectedVersion must be >= 1.");
        }

        if (matchingRound < 1)
        {
            throw new ArgumentException("matchingRound must be >= 1 when supplied.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            const string SelectForUpdate = """
                SELECT driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, created_at, updated_at, reserved_round
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
                    current = RehydrateFromReader(reader);
                }
            }

            if (current is null)
            {
                // Reader (and command) are disposed above: rollback never runs
                // while a command is still in progress on this connection.
                await tx.RollbackAsync(ct);
                return ReserveResult.NotFound();
            }

            // A finished lifecycle identity must never become fresh work again.
            // Otherwise a delayed completion could match a reused assignment.
            await using (var history = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM driver_lifecycle_effects
                WHERE driver_id = @driver AND (trip_id = @trip OR reservation_id = @reservation));
                """, conn, tx))
            {
                history.Parameters.AddWithValue("driver", driverId);
                history.Parameters.AddWithValue("trip", tripId);
                history.Parameters.AddWithValue("reservation", reservationId);
                if ((bool)(await history.ExecuteScalarAsync(ct))!)
                    return ReserveResult.Conflict(DriverErrors.StaleReservation,
                        "Trip or reservation identity already belongs to a started lifecycle.");
            }

            var replayed = current.Status == DriverStatus.Reserved
                && current.ReservedTripId == tripId
                && current.ReservationId == reservationId;

            try
            {
                current.Reserve(tripId, reservationId, expectedVersion, matchingRound);
            }
            catch (DriverDomainException ex)
            {
                await tx.RollbackAsync(ct);
                return ReserveResult.Conflict(ex.Code, ex.Message);
            }

            if (!replayed)
            {
                await StoreAsync(conn, tx, current, ct);

                // Durable intent travels with the winning reserve in THIS
                // transaction. A failure here rolls back the Driver row too,
                // so there is no committed reservation without its event.
                await _outbox.EnqueueReservedAsync(
                    conn, tx, current, tripId, reservationId, ct);
                await _outbox.EnqueueNotificationAsync(
                    conn, tx, current, tripId, reservationId,
                    RideHailing.Messaging.Contracts.NotificationMessage.Types.DriverTripAssigned, ct);
            }

            await tx.CommitAsync(ct);
            return replayed ? ReserveResult.Replayed(current) : ReserveResult.Won(current);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(ex.ConstraintName, "ux_drivers_reservation_id", StringComparison.Ordinal))
        {
            // Backstop for the documented cross-row reservation collision: the
            // caller reused one reservationId on two drivers, so exactly one
            // identity table row may hold it. NARROW on purpose (W08 hardening):
            // any OTHER unique violation is a system anomaly, not a domain
            // conflict — masking it here would let Matching advance the
            // candidate on a fabricated rejection instead of staying UNKNOWN.
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
    /// B-W12-01/04: instrumented exact-token release. A conflict means the
    /// current reservation fenced this stale release (DSI-004); an
    /// AVAILABLE-no-refs replay is an idempotent success and is never counted
    /// as a second mutation.
    /// </summary>
    public async Task<DriverMutationResult> TryReleaseAsync(
        Guid driverId, Guid tripId, Guid reservationId,
        CancellationToken ct = default)
    {
        using var activity = DriverTelemetry.Activities.StartActivity("driver.release", ActivityKind.Internal);
        activity?.SetTag("trip.id", tripId);
        activity?.SetTag("reservation.id", reservationId);

        try
        {
            var result = await TryReleaseCoreAsync(driverId, tripId, reservationId, ct);
            activity?.SetTag("driver.release.outcome", result.Outcome switch
            {
                DriverMutationOutcome.Applied => "applied",
                DriverMutationOutcome.Unchanged => "noop",
                DriverMutationOutcome.Conflict => "conflict",
                DriverMutationOutcome.DriverNotFound => "driver_not_found",
                _ => "unknown",
            });

            if (result.Outcome == DriverMutationOutcome.Conflict
                && DriverMetrics.IsStaleReleaseBlocked(result.ConflictCode))
            {
                _metrics?.StaleReleaseBlocked(
                    DriverMetrics.ReleaseReasonClass(result.ConflictCode));
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
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
    private async Task<DriverMutationResult> TryReleaseCoreAsync(
        Guid driverId, Guid tripId, Guid reservationId,
        CancellationToken ct)
    {
        if (driverId == Guid.Empty || tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new ArgumentException("driverId/tripId/reservationId must not be empty.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var result = await ReleaseInTransactionAsync(conn, tx, driverId, tripId, reservationId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<DriverMutationResult> ReleaseInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid driverId, Guid tripId,
        Guid reservationId, CancellationToken ct,
        RideHailing.Messaging.Contracts.MessageEnvelope<System.Text.Json.JsonElement>? command = null,
        int? matchingRound = null)
    {
        var current = await LoadForUpdateAsync(conn, tx, driverId, ct);
        if (current is null)
        {
            return DriverMutationResult.NotFound();
        }

        var effected = current.Status == DriverStatus.Reserved
            && current.ReservedTripId == tripId
            && current.ReservationId == reservationId;

        // Broker compensation needs current, durable four-field provenance.
        // Availability alone cannot confirm an exact cleanup obligation. Exact
        // command redelivery has already returned its committed receipt above.
        if (command is not null)
        {
            if (current.Status != DriverStatus.Reserved)
                return DriverMutationResult.Conflict("RELEASE_PROVENANCE_REQUIRED",
                    "No current reservation proves this compensation; reconcile the authoritative event.");
            if (effected && current.ReservationRound is null)
                return DriverMutationResult.Conflict("RESERVATION_ROUND_REQUIRED",
                    "Legacy reservation has no matching generation provenance.");
            if (effected && current.ReservationRound != matchingRound)
                return DriverMutationResult.Conflict("RESERVATION_ROUND_MISMATCH",
                    "Release generation does not match the current reservation.");
        }

        try
        {
            current.Release(tripId, reservationId);
        }
        catch (DriverDomainException ex)
        {
            return DriverMutationResult.Conflict(ex.Code, ex.Message);
        }

        if (effected)
        {
            await StoreAsync(conn, tx, current, ct);
            await _outbox.EnqueueReleasedAsync(conn, tx, current, tripId, reservationId, ct, command);
            await _outbox.EnqueueNotificationAsync(conn, tx, current, tripId, reservationId,
                RideHailing.Messaging.Contracts.NotificationMessage.Types.DriverReservationReleased, ct);
        }

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
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

        if (!already)
        {
            await StoreAsync(conn, tx, current, ct);
            await _outbox.EnqueueOfflineAsync(conn, tx, current, ct);
        }

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
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

        if (!already)
        {
            await StoreAsync(conn, tx, current, ct);
            await _outbox.EnqueueAvailableAsync(conn, tx, current, ct);
        }

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // L1: the live drivers row clears reservation_id when the ride starts
        // (RESERVED -> ON_TRIP) and stays cleared after completion, so status
        // alone cannot answer "did THIS reservation commit?". The durable
        // lifecycle ledger keeps the exact (driver_id, reservation_id) pair
        // (and its trip) across the lifecycle; an exact match is authoritative
        // historical ownership and must win over the current status. A
        // different reservation/trip is never inferred from status alone.
        const string Sql = """
            SELECT d.status, d.version, d.reserved_trip_id, d.reservation_id,
                   EXISTS (
                       SELECT 1 FROM driver_lifecycle_effects e
                       WHERE e.driver_id = d.driver_id
                         AND e.reservation_id = @reservation
                         AND e.phase IN ('STARTED', 'COMPLETED'))
            FROM drivers d WHERE d.driver_id = @id;
            """;
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("id", driverId);
        cmd.Parameters.AddWithValue("reservation", reservationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new ReservationQuery(ReservationQueryOutcome.NotFound, null);
        }

        var status = FromDb(reader.GetString(0));
        var version = reader.GetInt32(1);
        Guid? held = reader.IsDBNull(3) ? null : reader.GetGuid(3);
        var historicalOwnership = reader.GetBoolean(4);

        if (historicalOwnership)
        {
            return new ReservationQuery(ReservationQueryOutcome.OwnedByThisReservation, version);
        }

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
            SELECT driver_id, user_id, status, version, reserved_trip_id, reservation_id, active_trip_id, vehicle_id, created_at, updated_at, reserved_round
            FROM drivers WHERE driver_id = @id FOR UPDATE;
            """;
        await using var sel = new NpgsqlCommand(Sql, conn, tx);
        sel.Parameters.AddWithValue("id", driverId);
        await using var reader = await sel.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return RehydrateFromReader(reader);
    }

    /// <summary>
    /// Single rehydration point for every drivers-table read shape: the
    /// reserved_round provenance column travels with the row so the published
    /// event generation stays stable across restart (W08 matchingRound lineage).
    /// </summary>
    private static Driver RehydrateFromReader(NpgsqlDataReader reader)
    {
        return Driver.Rehydrate(
            reader.GetGuid(0), reader.GetGuid(1), FromDb(reader.GetString(2)), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
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
                updated_at = @updated,
                reserved_round = @round
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
        upd.Parameters.AddWithValue("round", (object?)driver.ReservationRound ?? DBNull.Value);
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
