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
    public Guid? ActiveTripId { get; private set; }
    public Guid? VehicleId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Driver(
        Guid driverId, Guid userId, DriverStatus status, int version,
        Guid? reservedTripId, Guid? reservationId, Guid? activeTripId, Guid? vehicleId,
        DateTimeOffset updatedAt)
    {
        DriverId = driverId;
        UserId = userId;
        Status = status;
        Version = version;
        ReservedTripId = reservedTripId;
        ReservationId = reservationId;
        ActiveTripId = activeTripId;
        VehicleId = vehicleId;
        UpdatedAt = updatedAt;
        AssertCoherence();
    }

    public static Driver Create(Guid driverId, Guid userId, Guid? vehicleId = null)
    {
        if (driverId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "driverId must not be empty.");
        }

        if (userId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "userId must not be empty.");
        }

        return new Driver(driverId, userId, DriverStatus.Offline, 1, null, null, null, vehicleId, DateTimeOffset.UtcNow);
    }

    // Rehydration from persistence. Same coherence rules as live transitions (DSI-002).
    public static Driver Rehydrate(
        Guid driverId, Guid userId, DriverStatus status, int version,
        Guid? reservedTripId, Guid? reservationId, Guid? activeTripId, Guid? vehicleId,
        DateTimeOffset updatedAt)
    {
        if (version < 1)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "version must be >= 1.");
        }

        return new Driver(driverId, userId, status, version,
            reservedTripId, reservationId, activeTripId, vehicleId, updatedAt);
    }

    public void AttachVehicle(Guid vehicleId)
    {
        if (vehicleId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "vehicleId must not be empty.");
        }

        VehicleId = vehicleId;
        Touch();
    }

    // OFFLINE -> AVAILABLE. AVAILABLE retry is idempotent (Doc 01 §4.4).
    public void GoOnline()
    {
        switch (Status)
        {
            case DriverStatus.Offline:
                Status = DriverStatus.Available;
                Touch();
                break;
            case DriverStatus.Available:
                break; // idempotent retry
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

    // AVAILABLE -> OFFLINE. OFFLINE retry idempotent. Active reservation blocks (Race B groundwork).
    public void GoOffline()
    {
        switch (Status)
        {
            case DriverStatus.Available:
                Status = DriverStatus.Offline;
                Touch();
                break;
            case DriverStatus.Offline:
                break; // idempotent retry
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

    // Domain-level reserve (state legality only). W6 adds the DB guarded write / OCC.
    public void Reserve(Guid tripId, Guid reservationId)
    {
        if (tripId == Guid.Empty || reservationId == Guid.Empty)
        {
            throw new DriverDomainException(DriverErrors.IllegalTransition, "tripId/reservationId must not be empty.");
        }

        switch (Status)
        {
            case DriverStatus.Available:
                Status = DriverStatus.Reserved;
                ReservedTripId = tripId;
                ReservationId = reservationId;
                Touch();
                break;
            case DriverStatus.Reserved:
                if (ReservedTripId == tripId && ReservationId == reservationId)
                {
                    break; // same-reservation idempotent replay (DSI-005)
                }

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

    // Exact-token release. Stale token throws WITHOUT mutating (fencing groundwork, W7 persists it).
    public void Release(Guid tripId, Guid reservationId)
    {
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
        Touch();
    }

    public void MarkTripStarted(Guid tripId, Guid reservationId)
    {
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
        Touch();
    }

    public void MarkTripCompleted(Guid tripId)
    {
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
    private void AssertCoherence()
    {
        switch (Status)
        {
            case DriverStatus.Available:
                if (ReservedTripId != null || ReservationId != null || ActiveTripId != null)
                {
                    throw new DriverDomainException(
                        DriverErrors.IllegalTransition,
                        "Coherence violation: AVAILABLE must not carry reservation/active refs.");
                }

                break;
            case DriverStatus.Offline:
                if (ReservedTripId != null || ReservationId != null || ActiveTripId != null)
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
                if (ActiveTripId == null || ReservedTripId != null || ReservationId != null)
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
