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
