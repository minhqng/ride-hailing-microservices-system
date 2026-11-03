using Microsoft.Extensions.Options;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using RideHailing.PaymentService.Application.Payment;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Integration.Payment;

namespace RideHailing.PaymentService.Infrastructure;

public sealed class PaymentReconciliationOptions
{
    public bool Enabled { get; set; }

    public int BatchSize { get; set; } = 20;

    public int PollIntervalSeconds { get; set; } = 10;

    public int ReviewAfterSeconds { get; set; } = 300;

    public void Validate()
    {
        if (BatchSize is < 1 or > 100)
            throw new InvalidOperationException("Reconciliation:BatchSize must be between 1 and 100.");
        if (PollIntervalSeconds is < 1 or > 60)
            throw new InvalidOperationException("Reconciliation:PollIntervalSeconds must be between 1 and 60.");
        if (ReviewAfterSeconds is < 60 or > 3600)
            throw new InvalidOperationException("Reconciliation:ReviewAfterSeconds must be between 60 and 3600.");
    }
}

/// <summary>
/// Bounded Production reconciliation: resolves UNKNOWN/PROCESSING operations
/// (and PENDING intents whose provider effect survived a local rollback)
/// against the independent provider ledger, under SKIP LOCKED fencing, and
/// publishes the terminal outcome through the same stable per-operation
/// Outbox identity used by the command processor. It never starts a second
/// financial operation and never invents a new operation key.
/// </summary>
public sealed class PaymentReconciliationWorker(
    PaymentExecution execution,
    ProviderLedger provider,
    NpgsqlOutboxStore outbox,
    TimeProvider time,
    IOptions<PaymentReconciliationOptions> options,
    ILogger<PaymentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Payment reconciliation worker is disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation(
            "Payment reconciliation worker started: batch {BatchSize}, interval {Interval}s.",
            options.Value.BatchSize, options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resolved = await ReconcileBatchAsync(stoppingToken);
                if (resolved > 0)
                {
                    logger.LogInformation(
                        "Payment reconciliation resolved {Count} operation(s).", resolved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Payment reconciliation batch failed; the next interval retries the same durable work.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> ReconcileBatchAsync(CancellationToken token)
    {
        var keys = await execution.ListReconciliationCandidatesAsync(
            options.Value.BatchSize, token);
        var resolved = 0;
        foreach (var key in keys)
        {
            var operation = await execution.TryReconcileAsync(
                key, provider, EnqueueOutcomeAsync,
                TimeSpan.FromSeconds(options.Value.ReviewAfterSeconds), token);
            if (operation is not null) resolved++;
        }
        return resolved;
    }

    private async Task EnqueueOutcomeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PaymentOperation operation,
        CancellationToken token)
    {
        var outcome = operation.Type switch
        {
            PaymentOperationType.Authorize => PaymentCommandHandlers.ToAuthorizeResult(operation),
            PaymentOperationType.Void => PaymentCommandHandlers.ToVoidResult(operation),
            PaymentOperationType.Capture => PaymentCommandHandlers.ToCaptureResult(operation),
            _ => throw new InvalidOperationException(
                $"Unsupported Payment operation type {operation.Type}."),
        };
        var descriptor = PaymentOutcomeContract.Describe(outcome);
        var envelope = MessageEnvelope<object>.Create(
            descriptor.MessageType,
            operation.TripId.ToString("D"),
            outcome,
            messageId: $"payment:{operation.Id:N}:{descriptor.MessageType}",
            occurredAt: time.GetUtcNow(),
            aggregateType: "Trip",
            aggregateId: operation.TripId.ToString("D"),
            operationKey: operation.OperationKey);
        await outbox.EnqueueAsync(connection, transaction,
            OutboxMessage.Create(envelope, RideTopology.EventsExchange, descriptor.RoutingKey), token);
    }
}
