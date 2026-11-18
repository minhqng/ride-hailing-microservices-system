// Trip HTTP route and method contract.
//
// The routes are frozen constants. The first behavior-backed slice is
// CreateRide, then GetTrip + CancelRide. The Driver-actor
// accept/start/complete transport uses the authenticated Driver principal,
// bound to the Driver-owned profile and the exact Trip assignment. The public
// Driver reject route is backed by the durable MATCH_FAILED/DRIVER_REJECTED
// policy and exact cleanup intents.
//
// FREEZE RATIONALE:
//   - Resource noun "/trips" (not "/rides"): aligns with the canonical
//     aggregate name Trip and the stable tripId identity, avoiding
//     a ride/trip synonym split across Trip-owned URLs. Handler names
//     (CreateRide/CancelRide) keep the user-facing verb; URLs keep the
//     authority noun.
//   - POST for CreateRide (not PUT): idempotency here is a domain property
//     backed by durable identity + Idempotency-Key header, not an HTTP-method
//     property. POST is the correct transport for "create one
//     logical ride, deduplicated by key" (semantics, not proven here).
//   - GET for GetTrip: safe read of one logical ride by stable identity.
//   - POST /trips/{id}/cancel (not DELETE): cancel is a guarded domain
//     transition with compensation/cleanup side effects, not a resource
//     deletion. Explicit sub-resource action keeps the terminal intent visible.
//   - No /v1 prefix: no versioned HTTP surface exists yet in the repo
//     (only /health/*, /info). Versioning stays future-additive; integration
//     message versioning (V1) is a separate platform concern.
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

    /// <summary>POST /trips/{tripId:guid}/accept — Driver accepts the current assignment.</summary>
    public const string AcceptDriver = "/trips/{tripId:guid}/accept";

    /// <summary>POST /trips/{tripId:guid}/reject — Driver rejects the current assignment.</summary>
    public const string RejectDriver = "/trips/{tripId:guid}/reject";

    /// <summary>POST /trips/{tripId:guid}/start — Driver starts the authorized ride.</summary>
    public const string StartTrip = "/trips/{tripId:guid}/start";

    /// <summary>POST /trips/{tripId:guid}/complete — Driver completes the in-progress ride.</summary>
    public const string CompleteTrip = "/trips/{tripId:guid}/complete";
}
