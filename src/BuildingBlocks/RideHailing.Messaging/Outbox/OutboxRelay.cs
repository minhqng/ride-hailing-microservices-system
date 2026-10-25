using RabbitMQ.Client.Exceptions;
using RideHailing.Messaging.Observability;

namespace RideHailing.Messaging.Outbox;

/// <summary>
/// Shared Outbox relay. Claim is a short DB transaction; publish happens with
/// no DB lock held; mark happens only after the publisher confirm. The same
/// messageId is retained across uncertain publication and relay restart.
/// </summary>
public sealed class OutboxRelay(
    IOutboxStore store,
    IOutboxPublisher publisher,
    OutboxRelayOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly IOutboxStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IOutboxPublisher publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly OutboxRelayOptions options = Validated(options);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public OutboxRelayOptions Options => options;

    public async Task<int> RunOnceAsync(CancellationToken token = default)
    {
        var claimed = await store.ClaimBatchAsync(options.RelayId, options.BatchSize, options.ClaimLease,
            clock.GetUtcNow(), token);
        if (claimed.Count == 0)
        {
            await ObserveBacklogAsync(token);
            return 0;
        }

        MessagingMetrics.RecordClaim(claimed.Count);
        foreach (var message in claimed)
        {
            token.ThrowIfCancellationRequested();

            // Test seam: process death after claim, before any publish.
            if (options.BeforePublish is not null)
                await options.BeforePublish(message, token);

            var started = clock.GetTimestamp();
            MessagingMetrics.RecordPublishAttempt();
            try
            {
                await publisher.PublishAsync(message, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                MessagingMetrics.RecordPublishFailed();
                await store.MarkFailedAsync(message, options.RelayId, SafeErrorCode(error), clock.GetUtcNow(),
                    options.RetryDelay, options.MaxAttempts, token);
                continue;
            }

            MessagingMetrics.RecordPublishAccepted();
            MessagingMetrics.RecordConfirmLatency(clock.GetElapsedTime(started).TotalMilliseconds);

            // Test seam: publish was accepted but the local mark is lost. The
            // row remains pending and a later cycle republishes the same id.
            if (options.AfterPublishBeforeMark is not null)
                await options.AfterPublishBeforeMark(message, token);

            await RideHailing.Messaging.FaultPoints.Current.HitAsync(
                RideHailing.Messaging.FaultPointNames.OutboxAfterPublishBeforeMark, token);

            var marked = await store.MarkPublishedAsync(message.MessageId, clock.GetUtcNow(), token);
            MessagingMetrics.RecordMark(marked);
        }

        await ObserveBacklogAsync(token);
        return claimed.Count;
    }

    public async Task RunAsync(CancellationToken token = default)
    {
        while (!token.IsCancellationRequested)
        {
            var claimed = await RunOnceAsync(token);
            if (claimed == 0)
                await Task.Delay(options.PollInterval, clock, token);
        }
    }

    private async Task ObserveBacklogAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var stats = await store.GetStatsAsync(token);
        MessagingMetrics.RecordOutboxBacklog(stats.PendingCount, stats.FailedVisibleCount,
            stats.OldestPendingAgeSeconds(now));
    }

    private static OutboxRelayOptions Validated(OutboxRelayOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return value;
    }

    private static string SafeErrorCode(Exception error) => error switch
    {
        PublishReturnException => "UNROUTABLE",
        BrokerUnreachableException => "BROKER_UNREACHABLE",
        TimeoutException => "PUBLISH_TIMEOUT",
        OperationCanceledException => "PUBLISH_CANCELLED",
        ArgumentException => "PUBLISH_REJECTED",
        _ => "PUBLISH_FAILED",
    };
}
