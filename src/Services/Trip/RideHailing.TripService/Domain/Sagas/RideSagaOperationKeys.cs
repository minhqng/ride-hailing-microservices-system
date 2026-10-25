// RideSaga operation-key lineage (Doc 05A §18.4, Doc 02 §15.1).
//
// One stable logical identity per Payment operation for the whole Saga
// lifetime (DSI-034): retries, redeliveries, relay recovery and process
// restarts all reuse the same key. A timeout NEVER mints a fresh key.

namespace RideHailing.TripService.Domain.Sagas;

/// <summary>
/// Frozen W10 operation-key formats. The database CHECK constraint on
/// ride_sagas enforces the same lineage at the storage layer.
/// </summary>
public static class RideSagaOperationKeys
{
    public static string Authorize(Guid tripId) => $"{tripId:D}:authorize:v1";

    public static string Capture(Guid tripId) => $"{tripId:D}:capture:v1";

    public static string Void(Guid tripId) => $"{tripId:D}:void:v1";
}
