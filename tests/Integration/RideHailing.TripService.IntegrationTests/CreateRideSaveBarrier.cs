// Deterministic CreateRide race synchronizer (W4, TEST-ONLY).
//
// Coordinates two independent TripDbContext instances so both competing
// handlers complete their idempotency/active-Trip pre-reads and local
// Trip/fare/idempotency construction BEFORE either authoritative transaction
// saves. Each side's interceptor signals arrival at SavingChangesAsync and
// waits until both sides have arrived; only then are both released into real
// PostgreSQL, which arbitrates the write conflict. No production handler,
// DbContext or configuration references this type.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RideHailing.TripService.IntegrationTests;

/// <summary>
/// Two-party asynchronous rendezvous for one deterministic race iteration.
/// Never blocks a thread: arrival is an interlocked count plus a
/// TaskCompletionSource released when both sides arrive.
/// </summary>
public sealed class TwoPartySaveBarrier
{
    private int _arrived;
    private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals arrival and asynchronously waits for the other side.</summary>
    public async Task SignalAndWaitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _arrived) == 2)
        {
            _bothArrived.TrySetResult();
        }

        await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// EF Core interceptor that rendezvous both race sides at the authoritative
/// write point. Registered per-test-context only.
/// </summary>
public sealed class SaveBarrierInterceptor : SaveChangesInterceptor
{
    private readonly TwoPartySaveBarrier _barrier;

    public SaveBarrierInterceptor(TwoPartySaveBarrier barrier)
    {
        _barrier = barrier;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await _barrier.SignalAndWaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }
}
