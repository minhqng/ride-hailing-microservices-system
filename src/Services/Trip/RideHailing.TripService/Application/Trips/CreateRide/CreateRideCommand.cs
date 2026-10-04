// CreateRide application command (A-W04-01).
//
// Semantic/trusted inputs only: the Rider identity arrives as an explicit
// application input (production auth extraction belongs to a later W4 slice),
// locations are domain TripLocation values, and the opaque client-supplied
// Idempotency-Key travels as an exact string. No correlation/trace identity,
// no HTTP context, no generated Trip identity, no fare, no timestamp: the
// handler derives those.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Application.Trips.CreateRide;

/// <summary>
/// One logical CreateRide request: who rides, where, and which client key
/// deduplicates the attempt.
/// </summary>
public sealed record CreateRideCommand(
    Guid RiderId,
    TripLocation Pickup,
    TripLocation Dropoff,
    string IdempotencyKey);
