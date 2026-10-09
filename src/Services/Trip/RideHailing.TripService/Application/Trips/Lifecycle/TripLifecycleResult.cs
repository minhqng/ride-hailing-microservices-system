// Shared W5 lifecycle intake result (A-W05 application coordination).
//
// Every W5 handler follows one shape: load the Trip, derive caller/context
// identity, call exactly one aggregate operation, persist the authoritative
// result, map the domain outcome. Handlers never duplicate FSM logic and
// never mutate assignment fields directly. Optimistic-concurrency conflicts
// (Version token) propagate as TripConcurrencyConflictException: the losing
// writer never answers from losing state, and retry ownership stays with the
// future workflow layer, never a blind handler retry.

using RideHailing.TripService.Domain.Trips;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

/// <summary>
/// Data-only result of one W5 lifecycle intake. Carries the Trip's current
/// business truth after handling so transport mapping and tests assert
/// persisted state, never status codes alone.
/// </summary>
public sealed record TripLifecycleResult
{
    /// <summary>True for applied and idempotent-replay outcomes.</summary>
    public bool IsSuccess { get; private init; }

    /// <summary>True when the same legal fact was already current (no mutation).</summary>
    public bool IsReplay { get; private init; }

    /// <summary>
    /// True when no owned Trip exists for the supplied identity. Never
    /// disclosed as a distinct owner-mismatch (SEC-03): missing and
    /// not-owned share this outcome.
    /// </summary>
    public bool IsNotFound { get; private init; }

    /// <summary>Acted-on logical ride.</summary>
    public Guid TripId { get; private init; }

    /// <summary>Current Trip business state after handling.</summary>
    public TripState State { get; private init; }

    /// <summary>Current matching generation after handling.</summary>
    public int CurrentMatchingRound { get; private init; }

    /// <summary>Current assigned Driver, or null when unassigned.</summary>
    public Guid? AssignedDriverId { get; private init; }

    /// <summary>Current reservation effect, or null when unassigned.</summary>
    public Guid? ReservationId { get; private init; }

    /// <summary>Future-work requirement exposed by the transition.</summary>
    public TripCompensation Compensation { get; private init; }

    /// <summary>Domain rejection code (<see cref="TripErrorCode.None"/> on success).</summary>
    public TripErrorCode ErrorCode { get; private init; }

    /// <summary>Domain diagnostic message (empty on success).</summary>
    public string Message { get; private init; } = string.Empty;

    /// <summary>Builds an applied/replay success from the persisted aggregate.</summary>
    public static TripLifecycleResult Success(Trip trip, bool isReplay, TripCompensation compensation) => new()
    {
        IsSuccess = true,
        IsReplay = isReplay,
        TripId = trip.TripId,
        State = trip.State,
        CurrentMatchingRound = trip.CurrentMatchingRound,
        AssignedDriverId = trip.Assignment?.DriverId,
        ReservationId = trip.Assignment?.ReservationId,
        Compensation = compensation,
    };

    /// <summary>Builds the not-owned/missing outcome.</summary>
    public static TripLifecycleResult NotFound() => new()
    {
        IsNotFound = true,
        ErrorCode = TripErrorCode.None,
        Message = "Trip was not found.",
    };

    /// <summary>Builds a domain-rejection outcome, preserving current Trip truth.</summary>
    public static TripLifecycleResult Fail(Trip trip, TripOutcome outcome) => new()
    {
        TripId = trip.TripId,
        State = trip.State,
        CurrentMatchingRound = trip.CurrentMatchingRound,
        AssignedDriverId = trip.Assignment?.DriverId,
        ReservationId = trip.Assignment?.ReservationId,
        Compensation = TripCompensation.None,
        ErrorCode = outcome.ErrorCode,
        Message = outcome.Message,
    };
}
