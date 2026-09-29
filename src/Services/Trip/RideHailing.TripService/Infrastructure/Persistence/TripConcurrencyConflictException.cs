// Trip optimistic-concurrency conflict (A-W03-07).
//
// Translates raw EF DbUpdateConcurrencyException into a Trip-specific
// persistence signal. Carries the conflicting Trip identity and, when
// available without brittle plumbing, the originally observed version.
// Contains no HTTP semantics and performs no retry: a stale write means
// authoritative state moved and the caller must reread before reevaluating
// business legality.

namespace RideHailing.TripService.Infrastructure.Persistence;

/// <summary>
/// Typed Trip persistence conflict for stale optimistic-concurrency writes.
/// </summary>
public sealed class TripConcurrencyConflictException : Exception
{
    /// <summary>Conflicting Trip identity.</summary>
    public Guid TripId { get; }

    /// <summary>Originally observed version, when determinable.</summary>
    public int? ExpectedVersion { get; }

    public TripConcurrencyConflictException(Guid tripId, int? expectedVersion, Exception? innerException = null)
        : base(
            $"Trip {tripId} was concurrently modified" +
            (expectedVersion.HasValue ? $" (expected version {expectedVersion.Value})" : string.Empty) +
            ". Reread authoritative state before reevaluating business legality.",
            innerException)
    {
        TripId = tripId;
        ExpectedVersion = expectedVersion;
    }
}
