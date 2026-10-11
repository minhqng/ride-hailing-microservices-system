// Trip HTTP route/method mechanical freeze.
//
// W2 froze the intended routes as documented constants; W5 registers the
// first behavior-backed slice (CreateRide since W4; GetTrip + CancelRide
// since W5) in Program.cs. Driver-actor lifecycle routes (accept/start/
// complete) remain future: their transport identity is Driver-fenced and the
// edge/middleware owner is not Trip, so no route is invented for them here.
//
// W2 MECHANICAL FREEZE — reasons:
//   - Resource noun "/trips" (not "/rides"): aligns with the canonical
//     aggregate name Trip (Doc 01 §6) and the stable tripId identity, avoiding
//     a ride/trip synonym split across Trip-owned URLs. Handler names
//     (CreateRide/CancelRide) keep the user-facing verb; URLs keep the
//     authority noun.
//   - POST for CreateRide (not PUT): idempotency here is a domain property
//     backed by durable identity + Idempotency-Key header, not an HTTP-method
//     property (Doc 00 §2.11). POST is the correct transport for "create one
//     logical ride, deduplicated by key" (W4 semantics, not proven here).
//   - GET for GetTrip: safe read of one logical ride by stable identity.
//   - POST /trips/{id}/cancel (not DELETE): cancel is a guarded domain
//     transition with compensation/cleanup side effects, not a resource
//     deletion. Explicit sub-resource action keeps the terminal intent visible.
//   - No /v1 prefix: no versioned HTTP surface exists yet in the repo
//     (only /health/*, /info). Versioning stays future-additive; integration
//     message versioning (V1) is a separate C-owned concern (Doc 02 §6).
//   - Route constraint "{tripId:guid}" documents that tripId is a Guid on the
//     wire (uuid string form), consistent with StableIdentities.

namespace RideHailing.TripService.Api;

/// <summary>
/// Intended Trip HTTP routes and methods. CreateRide, GetTrip and CancelRide
/// are registered in Program.cs with behavior-backed handlers; any route
/// added here in the future must follow the same rule.
/// </summary>
public static class TripRoutes
{
    /// <summary>POST /trips — CreateRide (transport shape only).</summary>
    public const string CreateRide = "/trips";

    /// <summary>GET /trips/{tripId:guid} — GetTrip (transport shape only).</summary>
    public const string GetTrip = "/trips/{tripId:guid}";

    /// <summary>POST /trips/{tripId:guid}/cancel — CancelRide (transport shape only).</summary>
    public const string CancelRide = "/trips/{tripId:guid}/cancel";
}
