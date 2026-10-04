// CreateRide application handler (A-W04-01): deterministic stable create
// with request-idempotency persistence and the DSI-013 advisory precheck.
//
// ALGORITHM. Replay precedence: an existing (scope,key) record resolves BEFORE
// any active-Trip check, so a response-loss retry on an active Trip still
// replays. Same key + same fingerprint returns the canonical Trip with
// IsReplay; same key + different fingerprint is IDEMPOTENCY_CONFLICT. Only on
// a key miss does the advisory active-Trip query run (clarity only — the
// PostgreSQL partial unique index is the race-safe DSI-013 defense; the
// precheck alone proves nothing under concurrency).
//
// NEW CREATE: one TimeProvider timestamp feeds Trip.CreatedAt,
// FareSnapshot.CreatedAt and the idempotency record; Trip, FareSnapshot
// (inside the Trip row) and the idempotency record commit in ONE
// SaveChangesAsync (all-or-nothing, no manual transaction ceremony).
//
// RACES: expected 23505 violations on the idempotency composite key or the
// active-Rider index are reconciled against committed database truth (never
// blind-retried, never answered from the losing local TripId). Either guard
// may fail first under concurrent writes, so the constraint name only
// classifies "expected W4 create race"; the final result always comes from
// re-reading (scope,key) first, then the Rider's active Trip.

using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Application.Trips.CreateRide;

/// <summary>
/// Deterministic CreateRide application core. No HTTP dependency; the Rider
/// identity arrives as explicit trusted application input.
/// </summary>
public sealed class CreateRideHandler
{
    /// <summary>Composite-key/unique constraint on the idempotency table.</summary>
    internal const string IdempotencyConstraintName = "PK_request_idempotency";

    /// <summary>Partial unique index defending one active Trip per Rider.</summary>
    internal const string ActiveRiderConstraintName = "UX_trips_one_active_per_rider";

    private readonly TripDbContext _db;
    private readonly TimeProvider _time;

    public CreateRideHandler(TripDbContext db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _time = time;
    }

    /// <summary>
    /// Executes one logical CreateRide: replay, conflict or new creation.
    /// </summary>
    public async Task<CreateRideResult> HandleAsync(CreateRideCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.RiderId == Guid.Empty)
        {
            return CreateRideResult.Fail(CreateRideError.Validation, "CreateRide requires a non-empty rider identity.");
        }

        if (command.Pickup is null || command.Dropoff is null)
        {
            return CreateRideResult.Fail(CreateRideError.Validation, "CreateRide requires pickup and dropoff locations.");
        }

        // The accepted key is an OPAQUE EXACT identity: required, non-blank,
        // max 128 chars, no case/whitespace/UUID normalization.
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return CreateRideResult.Fail(CreateRideError.Validation, "CreateRide requires a non-empty Idempotency-Key.");
        }

        if (command.IdempotencyKey.Length > CreateRideRequestFingerprint.MaxKeyLength)
        {
            return CreateRideResult.Fail(
                CreateRideError.Validation,
                $"Idempotency-Key exceeds {CreateRideRequestFingerprint.MaxKeyLength} chars.");
        }

        var scope = CreateRideRequestFingerprint.BuildScope(command.RiderId);
        var fingerprint = CreateRideRequestFingerprint.Compute(command.RiderId, command.Pickup, command.Dropoff);

        // FAST REPLAY PATH: existing record resolves before any active check.
        // No fare calculation, no new TripId, no mutation on this path.
        var existing = await _db.RequestIdempotency.AsNoTracking()
            .SingleOrDefaultAsync(
                r => r.Scope == scope && r.Key == command.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return CreateRideResult.Fail(
                    CreateRideError.IdempotencyConflict,
                    "Idempotency-Key was already used for a different CreateRide request.");
            }

            var replayed = await _db.Trips.AsNoTracking()
                .SingleOrDefaultAsync(t => t.TripId == existing.ResourceId, cancellationToken)
                .ConfigureAwait(false);

            if (replayed is null)
            {
                throw new InvalidOperationException(
                    $"Request-idempotency record ({scope}) references missing Trip {existing.ResourceId}.");
            }

            return CreateRideResult.Success(replayed.TripId, isReplay: true);
        }

        // NEW CREATE PATH: advisory active-Trip clarity check (NOT concurrency
        // authority; the partial unique index defends the race).
        var hasActive = await _db.Trips.AsNoTracking()
            .AnyAsync(
                t => t.RiderId == command.RiderId && TripStatePolicy.ActiveForRiderBooking.Contains(t.State),
                cancellationToken)
            .ConfigureAwait(false);

        if (hasActive)
        {
            return CreateRideResult.Fail(
                CreateRideError.ActiveTripConflict,
                "Rider already holds an active Trip (DSI-013).");
        }

        var now = _time.GetUtcNow();
        var fare = DeterministicFareCalculator.Calculate(command.Pickup, command.Dropoff, now);
        var tripId = Guid.NewGuid();
        var trip = Trip.Create(tripId, command.RiderId, command.Pickup, command.Dropoff, fare, now);
        var record = new RequestIdempotencyRecord(scope, command.IdempotencyKey, fingerprint, tripId, now);

        _db.Trips.Add(trip);
        _db.RequestIdempotency.Add(record);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsExpectedCreateRace(ex))
        {
            return await ReconcileRaceAsync(scope, command.IdempotencyKey, fingerprint, command.RiderId, cancellationToken)
                .ConfigureAwait(false);
        }

        return CreateRideResult.Success(tripId, isReplay: false);
    }

    private static bool IsExpectedCreateRace(DbUpdateException ex)
    {
        for (var current = (Exception?)ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg &&
                pg.SqlState == PostgresErrorCodes.UniqueViolation &&
                (pg.ConstraintName == IdempotencyConstraintName ||
                 pg.ConstraintName == ActiveRiderConstraintName))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<CreateRideResult> ReconcileRaceAsync(
        string scope,
        string key,
        string fingerprint,
        Guid riderId,
        CancellationToken cancellationToken)
    {
        // Detach the losing Added entities so reconciliation reads committed
        // truth; never answer from the losing locally-generated TripId.
        _db.ChangeTracker.Clear();

        var committed = await _db.RequestIdempotency.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Scope == scope && r.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (committed is not null)
        {
            if (!string.Equals(committed.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return CreateRideResult.Fail(
                    CreateRideError.IdempotencyConflict,
                    "Idempotency-Key was already used for a different CreateRide request.");
            }

            var winner = await _db.Trips.AsNoTracking()
                .SingleOrDefaultAsync(t => t.TripId == committed.ResourceId, cancellationToken)
                .ConfigureAwait(false);

            if (winner is null)
            {
                throw new InvalidOperationException(
                    $"Request-idempotency record ({scope}) references missing Trip {committed.ResourceId}.");
            }

            return CreateRideResult.Success(winner.TripId, isReplay: true);
        }

        var activeExists = await _db.Trips.AsNoTracking()
            .AnyAsync(
                t => t.RiderId == riderId && TripStatePolicy.ActiveForRiderBooking.Contains(t.State),
                cancellationToken)
            .ConfigureAwait(false);

        if (activeExists)
        {
            return CreateRideResult.Fail(
                CreateRideError.ActiveTripConflict,
                "Rider already holds an active Trip (DSI-013).");
        }

        throw new InvalidOperationException(
            "CreateRide hit an expected uniqueness guard but neither the idempotency record nor an active Trip is committed.");
    }
}
