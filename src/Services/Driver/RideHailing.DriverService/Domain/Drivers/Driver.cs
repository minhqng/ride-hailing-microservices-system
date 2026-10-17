// B-W03-01: Driver aggregate — local authority state machine (Doc 01 §4.2-4.5).
// W3 scope: pure domain transitions + DSI-002 coherence. No Redis, no OCC,
// no UNKNOWN, no Outbox. Concurrency guards (W6) and fencing persistence (W7) come later.
// Version here is a simple bump-on-change counter, NOT an OCC token yet.
namespace RideHailing.DriverService.Domain.Drivers;

public sealed class Driver
{
    public Guid DriverId { get; }
    public Guid UserId { get; }
    public DriverStatus Status { get; private set; }
    public int Version { get; private set; }
    public Guid? ReservedTripId { get; private set; }
    public Guid? ReservationId { get; private set; }
    /// <summary>
    /// Matching generation that produced the current reservation identity
    /// (provenance for DriverReservedV1.matchingRound). Write-once at
    /// the winning reserve; cleared with the reservation refs on release and
    /// trip start. Historical rows may legitimately carry NULL — provenance
    /// that predates this column is unknown and is never fabricated; their
    /// events omit matchingRound (payload omission means "round unknown",
    /// which Trip parks for review).
    /// </summary>
    public int? ReservationRound { get; private set; }
    public Guid? ActiveTripId { get; private set; }
    public Guid? VehicleId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Driver(
        Guid driverId, Guid userId, DriverStatus status, int version,
        Guid? reservedTripId, Guid? reservationId, int? reservationRound,
        Guid? activeTripId, Guid? vehicleId,
        DateTimeOffset updatedAt)
    {
        DriverId = driverId;
        UserId = userId;
        Status = status;
        Version = version;
        ReservedTripId = reservedTripId;
        ReservationId = reservationId;
        ReservationRound = reservationRound;
        ActiveTripId = activeTripId;
        VehicleId = vehicleId;
        UpdatedAt = updatedAt;
        AssertCoherence();
    }

    public static Driver Create(Guid driverId, Guid userId, Guid? vehicleId = null)
    {
        if (driverId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "driverId must not be empty.");
        }

        if (userId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "userId must not be empty.");
        }

        if (vehicleId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "vehicleId must not be empty.");
        }

        return new Driver(driverId, userId, DriverStatus.Offline, 1, null, null, null, null, vehicleId, DateTimeOffset.UtcNow);
    }

    // Rehydration from persistence. Same coherence rules as live transitions (DSI-002).
    public static Driver Rehydrate(
        Guid driverId, Guid userId, DriverStatus status, int version,
        Guid? reservedTripId, Guid? reservationId, int? reservationRound,
        Guid? activeTripId, Guid? vehicleId,
        DateTimeOffset updatedAt)
    {
        if (driverId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "driverId must not be empty.");
        }

        if (userId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "userId must not be empty.");
        }

        // Optional refs must be real identities when present: Guid.Empty is
        // corrupt data, not "no reservation" (null means absent).
        if (reservedTripId == Guid.Empty || reservationId == Guid.Empty
            || activeTripId == Guid.Empty || vehicleId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "reservation/active/vehicle refs must not be empty.");
        }

        if (version < 1)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "version must be >= 1.");
        }

        if (reservationRound < 1)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "reservationRound must be >= 1 when present.");
        }

        return new Driver(driverId, userId, status, version,
            reservedTripId, reservationId, reservationRound, activeTripId, vehicleId, updatedAt);
    }

    public void AttachVehicle(Guid vehicleId)
    {
        if (vehicleId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "vehicleId must not be empty.");
        }

        VehicleId = vehicleId;
        Touch();
    }

    // OFFLINE -> AVAILABLE with OCC fence (B-W07-04 Race-B family, DSI-003).
    // Precedence mirrors Reserve: idempotent retry first (already AVAILABLE
    // succeeds regardless of version — the desired end-state already holds),
    // then the version fence (a stale caller must not overwrite newer
    // authority), then state legality. A blind full-row overwrite of a newer
    // RESERVED row is exactly the lost-reservation shape DSI-003 forbids.
    public void GoOnline(int expectedVersion)
    {
        if (expectedVersion < 1)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "expectedVersion must be >= 1.");
        }

        if (Status == DriverStatus.Available)
        {
            return; // idempotent retry: end-state already holds, no new effect.
        }

        if (Version != expectedVersion)
        {
            throw new DriverDomainException(
                DriverErrors.StaleVersion,
                $"GoOnline fenced: expected version {expectedVersion}, current version {Version}.");
        }

        switch (Status)
        {
            case DriverStatus.Offline:
                Status = DriverStatus.Available;
                Touch();
                break;
            case DriverStatus.Reserved:
            case DriverStatus.OnTrip:
                throw new DriverDomainException(
                    DriverErrors.IllegalTransition,
                    $"GoOnline illegal from {Status}.");
            default:
                throw new DriverDomainException(
                    DriverErrors.IllegalTransition, $"Unknown status {Status}.");
        }
    }

    // AVAILABLE -> OFFLINE with OCC fence (B-W07-04 Race B, DSR-004/DSI-003).
    // Same precedence: already-OFFLINE replay succeeds version-agnostic, then
    // the fence, then legality (RESERVED with an active reservation rejects;
    // only the guarded path may decide offline-vs-reserved).
    public void GoOffline(int expectedVersion)
    {
        if (expectedVersion < 1)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "expectedVersion must be >= 1.");
        }

        if (Status == DriverStatus.Offline)
        {
            return; // idempotent retry: end-state already holds, no new effect.
        }

        if (Version != expectedVersion)
        {
            throw new DriverDomainException(
                DriverErrors.StaleVersion,
                $"GoOffline fenced: expected version {expectedVersion}, current version {Version}.");
        }

        switch (Status)
        {
            case DriverStatus.Available:
                Status = DriverStatus.Offline;
                Touch();
                break;
            case DriverStatus.Reserved:
                throw new DriverDomainException(
                    DriverErrors.ReservationActive,
                    "GoOffline rejected: reservation active.");
            case DriverStatus.OnTrip:
                throw new DriverDomainException(
                    DriverErrors.IllegalTransition,
                    "GoOffline illegal while ON_TRIP.");
            default:
                throw new DriverDomainException(
                    DriverErrors.IllegalTransition, $"Unknown status {Status}.");
        }
    }

    // Domain-level reserve with OCC fence (B-W06-01/02/03, Doc 01 §1.4/§4.4,
    // DSI-001/003/005). Precedence is load-bearing:
    //   1. empty identity rejection (never a candidate effect);
    //   2. same-reservation replay (DSI-005): the effect already exists, so
    //      return canonical success WITHOUT consulting expectedVersion — a
    //      retry carries the version it originally saw;
    //   3. expected-version fence (DSI-003): a stale caller must not overwrite
    //      newer authority even when the driver happens to be AVAILABLE again;
    //   4. state legality (DSI-001): only AVAILABLE can be taken.
    // matchingRound (W08 hardening): the matching generation that produced this
    // reservation identity, recorded with the win and published as
    // DriverReservedV1.matchingRound (Doc 02 §21.3). Write-once wins: a replay
    // never overwrites or backfills the recorded generation, so one logical
    // reservation publishes ONE stable round across restart/replay/heal (a
    // backfilled legacy row would change the event content under the same
    // deterministic messageId and poison Trip's content-conflict guard).
    // A null round means the caller supplies no provenance: it stays null and
    // the event omits the field (Trip parks it for review) — never guessed
    // from mutable state, never defaulted to 1, never traded during replay.
    public void Reserve(Guid tripId, Guid reservationId, int expectedVersion, int? matchingRound = null)
    {
        if (tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "tripId/reservationId must not be empty.");
        }

        if (expectedVersion < 1)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "expectedVersion must be >= 1.");
        }

        if (matchingRound < 1)
        {
            throw new DriverDomainException(
                DriverErrors.IllegalTransition,
                "matchingRound must be a positive generation when supplied.");
        }

        if (Status == DriverStatus.Reserved
            && ReservedTripId == tripId && ReservationId == reservationId)
        {
            return; // same-reservation idempotent replay (DSI-005): no new effect, provenance holds.
        }

        if (Version != expectedVersion)
        {
            throw new DriverDomainException(
                DriverErrors.StaleVersion,
                $"Reserve fenced: expected version {expectedVersion}, current version {Version}.");
        }

        switch (Status)
        {
            case DriverStatus.Available:
                Status = DriverStatus.Reserved;
                ReservedTripId = tripId;
                ReservationId = reservationId;
                ReservationRound = matchingRound;
                Touch();
                break;
            case DriverStatus.Reserved:
                throw new DriverDomainException(
                    DriverErrors.DriverNotAvailable,
                    "Driver already reserved for another trip/reservation.");
            case DriverStatus.Offline:
            case DriverStatus.OnTrip:
                throw new DriverDomainException(
                    DriverErrors.DriverNotAvailable,
                    $"Reserve illegal: driver is {Status}.");
            default:
                throw new DriverDomainException(
                    DriverErrors.IllegalTransition, $"Unknown status {Status}.");
        }
    }

    // Exact-token release with fencing (B-W07-01/02, DSI-004/033, R-06).
    // Rule: release returns success iff the AVAILABLE-no-refs end-state holds
    // or is achieved by this call; a DIFFERENT live reservation conflicts and
    // is never freed. Concretely: exact token on RESERVED frees (effect);
    // anything on coherent AVAILABLE (no refs) is an idempotent cleanup
    // no-op (duplicate delivery, retried compensation); token mismatch on a
    // live RESERVED row throws WITHOUT mutating (fencing); ON_TRIP rejects.
    // Release carries no version: token equality on the locked fresh row is
    // the complete guard.
    // Malformed (empty) tokens are rejected as invalid input BEFORE state
    // evaluation: they can never match a real reservation.
    public void Release(Guid tripId, Guid reservationId)
    {
        if (tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "tripId/reservationId must not be empty.");
        }

        if (Status == DriverStatus.Available)
        {
            return; // end-state already holds: idempotent cleanup no-op.
        }

        if (Status != DriverStatus.Reserved)
        {
            throw new DriverDomainException(
                DriverErrors.IllegalTransition,
                $"Release illegal: driver is {Status}.");
        }

        if (ReservedTripId != tripId || ReservationId != reservationId)
        {
            throw new DriverDomainException(
                DriverErrors.StaleReservation,
                "Release token mismatch: stale release must not free current reservation.");
        }

        Status = DriverStatus.Available;
        ReservedTripId = null;
        ReservationId = null;
        ReservationRound = null;
        Touch();
    }

    public void MarkTripStarted(Guid tripId, Guid reservationId)
    {
        if (tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "tripId/reservationId must not be empty.");
        }

        if (Status != DriverStatus.Reserved)
        {
            throw new DriverDomainException(
                DriverErrors.IllegalTransition,
                $"StartTrip illegal: driver is {Status}.");
        }

        if (ReservedTripId != tripId || ReservationId != reservationId)
        {
            throw new DriverDomainException(
                DriverErrors.TripMismatch,
                "StartTrip identity mismatch.");
        }

        Status = DriverStatus.OnTrip;
        ActiveTripId = tripId;
        ReservedTripId = null;
        ReservationId = null;
        ReservationRound = null;
        Touch();
    }

    public void MarkTripCompleted(Guid tripId)
    {
        if (tripId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.InvalidIdentity, "tripId must not be empty.");
        }

        if (Status != DriverStatus.OnTrip)
        {
            throw new DriverDomainException(
                DriverErrors.IllegalTransition,
                $"CompleteTrip illegal: driver is {Status}.");
        }

        if (ActiveTripId != tripId)
        {
            throw new DriverDomainException(
                DriverErrors.TripMismatch,
                "CompleteTrip trip mismatch.");
        }

        Status = DriverStatus.Available;
        ActiveTripId = null;
        Touch();
    }

    // DSI-002: state/reservation coherence. Never persist AVAILABLE+reservationId etc.
    // The reservation round (W08 provenance) follows the same lifecycle: only
    // RESERVED may carry it (NULL allowed for historical rows); AVAILABLE,
    // OFFLINE and ON_TRIP must have cleared it together with the refs.
    private void AssertCoherence()
    {
        switch (Status)
        {
            case DriverStatus.Available:
                if (ReservedTripId != null || ReservationId != null || ActiveTripId != null
                    || ReservationRound != null)
                {
                    throw new DriverDomainException(
                        DriverErrors.IllegalTransition,
                        "Coherence violation: AVAILABLE must not carry reservation/active refs.");
                }

                break;
            case DriverStatus.Offline:
                if (ReservedTripId != null || ReservationId != null || ActiveTripId != null
                    || ReservationRound != null)
                {
                    throw new DriverDomainException(
                        DriverErrors.IllegalTransition,
                        "Coherence violation: OFFLINE must not carry reservation/active refs.");
                }

                break;
            case DriverStatus.Reserved:
                if (ReservedTripId == null || ReservationId == null || ActiveTripId != null)
                {
                    throw new DriverDomainException(
                        DriverErrors.IllegalTransition,
                        "Coherence violation: RESERVED requires trip+reservation and no active trip.");
                }

                break;
            case DriverStatus.OnTrip:
                if (ActiveTripId == null || ReservedTripId != null || ReservationId != null
                    || ReservationRound != null)
                {
                    throw new DriverDomainException(
                        DriverErrors.IllegalTransition,
                        "Coherence violation: ON_TRIP requires active trip and no reservation refs.");
                }

                break;
            default:
                throw new DriverDomainException(
                    DriverErrors.IllegalTransition, $"Unknown status {Status}.");
        }
    }

    private void Touch()
    {
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
        AssertCoherence();
    }
}
