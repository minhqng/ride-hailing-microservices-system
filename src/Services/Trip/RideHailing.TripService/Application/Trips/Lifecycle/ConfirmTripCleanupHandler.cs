using Microsoft.EntityFrameworkCore;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Application.Trips.Lifecycle;

public enum CleanupAuthorityResult
{
    Unknown, Released, ExactReservationNoLongerOwned, MatchingStopped, PaymentResolved
}

/// <summary>
/// Internal confirmation seam for the future workflow adapter. Inputs must be
/// conclusive authority results for this exact intent, never send/ACK success.
/// No HTTP route or invented Driver/Payment transport is exposed in W7.
/// </summary>
public sealed class ConfirmTripCleanupHandler(TripDbContext db, TimeProvider clock)
{
    public async Task<TripLifecycleResult> HandleAsync(Guid tripId, string intentKey,
        CleanupAuthorityResult authorityResult, CancellationToken cancellationToken = default)
    {
        var trip = await db.Trips.SingleOrDefaultAsync(t => t.TripId == tripId, cancellationToken);
        if (trip is null) return TripLifecycleResult.NotFound();
        var intent = trip.CleanupIntents.SingleOrDefault(i => i.Key == intentKey);
        var conclusive = intent?.Kind switch
        {
            TripCleanupKind.ReleaseReservation => authorityResult is CleanupAuthorityResult.Released
                or CleanupAuthorityResult.ExactReservationNoLongerOwned,
            TripCleanupKind.CancelMatching => authorityResult == CleanupAuthorityResult.MatchingStopped,
            TripCleanupKind.ReconcilePayment => authorityResult == CleanupAuthorityResult.PaymentResolved,
            _ => false
        };
        if (!conclusive)
            return TripLifecycleResult.Fail(trip, TripOutcome.Fail(TripErrorCode.StaleIdentity,
                "Cleanup remains pending until the matching authority confirms this exact obligation."));
        var outcome = trip.ConfirmCleanup(intentKey, clock.GetUtcNow());
        if (!outcome.IsSuccess) return TripLifecycleResult.Fail(trip, outcome);
        if (!outcome.IsReplay) await db.SaveChangesAsync(cancellationToken);
        return TripLifecycleResult.Success(trip, outcome.IsReplay, outcome.Compensation);
    }
}
