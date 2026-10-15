namespace RideHailing.TripService.Domain.Trips;

public enum TripCleanupKind { ReleaseReservation, CancelMatching, ReconcilePayment }

/// <summary>
/// Durable Trip-owned obligation, not proof of an external effect. Payment
/// reconciliation must recover Saga operation lineage before issuing a void.
/// </summary>
public sealed class TripCleanupIntent
{
    public Guid TripId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public TripCleanupKind Kind { get; private set; }
    public Guid? DriverId { get; private set; }
    public Guid? ReservationId { get; private set; }
    public int MatchingRound { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private TripCleanupIntent() { }

    internal static TripCleanupIntent Create(Guid tripId, TripCleanupKind kind,
        int round, DateTimeOffset now, Guid? driverId = null, Guid? reservationId = null)
    {
        if (tripId == Guid.Empty || now == default || round < 1)
            throw new ArgumentException("Cleanup requires a Trip, opened round and timestamp.");
        if (kind == TripCleanupKind.ReleaseReservation &&
            (driverId is null || driverId == Guid.Empty || reservationId is null || reservationId == Guid.Empty))
            throw new ArgumentException("Release requires exact Trip, Driver and reservation identities.");
        return new()
        {
            TripId = tripId, Kind = kind, MatchingRound = round, CreatedAt = now,
            DriverId = driverId, ReservationId = reservationId,
            Key = kind switch
            {
                TripCleanupKind.ReleaseReservation => $"release:{reservationId:D}",
                TripCleanupKind.CancelMatching => $"matching:{round}",
                TripCleanupKind.ReconcilePayment => "payment",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            }
        };
    }

    internal void Complete(DateTimeOffset now) => CompletedAt ??= now;
}
