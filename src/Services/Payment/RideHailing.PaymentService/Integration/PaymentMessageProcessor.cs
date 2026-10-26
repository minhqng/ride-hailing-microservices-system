using System.Text.Json;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Dedup;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using RideHailing.PaymentService.Application.Payment;
using RideHailing.PaymentService.Domain;
using RideHailing.PaymentService.Infrastructure;

namespace RideHailing.PaymentService.Integration.Payment;

/// <summary>
/// Payment command boundary. The processed-message marker, authoritative Payment
/// mutation and result Outbox intent commit in one payment_db transaction.
/// </summary>
public sealed class PaymentMessageProcessor(
    PaymentExecution execution,
    ProcessedMessageStore processedMessages,
    NpgsqlOutboxStore outbox,
    TimeProvider time,
    ILogger<PaymentMessageProcessor> logger)
{
    public const string ConsumerScope = "payment.commands.v1";
    public static readonly IReadOnlySet<string> SupportedMessageTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "AuthorizePaymentV1",
        "VoidPaymentAuthorizationV1",
        "CapturePaymentV1",
    };

    public async Task ProcessAsync(MessageEnvelope<JsonElement> envelope, CancellationToken token)
    {
        try
        {
            envelope.Validate();
            var command = DeserializeCommand(envelope);
            ValidateIdentity(envelope, command.TripId);
            var request = PaymentOperation.Create(command.OperationKey, command.TripId, command.Type,
                command.AmountMinor, command.Currency, time.GetUtcNow());

            var executionResult = await execution.ExecuteMessageAsync(
                request,
                PaymentSimulatorMode.Success,
                (connection, transaction, ct) => processedMessages.TryMarkAsync(
                    connection, transaction, ConsumerScope, envelope.MessageId, envelope.MessageType,
                    time.GetUtcNow(), handlerVersion: "w10-v1", aggregateId: command.TripId.ToString("D"), token: ct),
                async (connection, transaction, operation, ct) =>
                {
                    var outcome = command.ToOutcome(operation);
                    var descriptor = Describe(outcome);
                    var resultEnvelope = MessageEnvelope<object>.Create(
                        descriptor.MessageType,
                        envelope.CorrelationId,
                        outcome,
                        causationId: envelope.MessageId,
                        // Stable across a producer retry that accidentally uses a new command messageId.
                        // A later semantic outcome (for example pending -> captured) has a different type.
                        messageId: $"payment:{operation.Id:N}:{descriptor.MessageType}",
                        occurredAt: time.GetUtcNow(),
                        aggregateType: "Trip",
                        aggregateId: command.TripId.ToString("D"),
                        sagaId: envelope.SagaId,
                        operationKey: command.OperationKey);
                    await outbox.EnqueueAsync(connection, transaction,
                        OutboxMessage.Create(resultEnvelope, RideTopology.EventsExchange, descriptor.RoutingKey), ct);
                },
                token);

            logger.LogInformation(
                "Payment command {MessageType} {MessageId} key {OperationKey} completed as {State}; applied={Applied}.",
                envelope.MessageType, envelope.MessageId, command.OperationKey,
                executionResult.Operation.State, executionResult.Applied);
        }
        catch (PaymentConflictException)
        {
            throw new DeliveryFailureException(FailureKind.DomainConflict, "PAYMENT_IDEMPOTENCY_CONFLICT");
        }
        catch (JsonException)
        {
            throw new DeliveryFailureException(FailureKind.Poison, "INVALID_PAYMENT_COMMAND");
        }
        catch (ArgumentException)
        {
            throw new DeliveryFailureException(FailureKind.Poison, "INVALID_PAYMENT_COMMAND");
        }
    }

    private static PaymentCommand DeserializeCommand(MessageEnvelope<JsonElement> envelope)
    {
        // The envelope is the sole authority for operation identity. A payload
        // may omit it (Trip's frozen W10 producer) or echo it, but never
        // override it.
        var operationKey = envelope.OperationKey;
        if (string.IsNullOrWhiteSpace(operationKey))
            throw new ArgumentException("Payment command requires an envelope operationKey.");

        return envelope.MessageType switch
        {
            "AuthorizePaymentV1" => FromAuthorize(Deserialize<AuthorizePaymentV1>(envelope.Payload), operationKey),
            "VoidPaymentAuthorizationV1" => FromVoid(Deserialize<VoidPaymentAuthorizationV1>(envelope.Payload), operationKey),
            "CapturePaymentV1" => FromCapture(Deserialize<CapturePaymentV1>(envelope.Payload), operationKey),
            _ => throw new ArgumentException("Unsupported Payment command type."),
        };
    }

    private static T Deserialize<T>(JsonElement payload) where T : class =>
        JsonSerializer.Deserialize<T>(payload.GetRawText())
        ?? throw new JsonException("Payment command payload is required.");

    private static PaymentCommand FromAuthorize(AuthorizePaymentV1 value, string operationKey)
    {
        if (value.PaymentType != PaymentOperationType.Authorize)
            throw new ArgumentException("AuthorizePaymentV1 must use the Authorize operation type.");
        RequirePayloadKeyEcho(value.OperationKey, operationKey);
        return new(value.TripId, operationKey, PaymentOperationType.Authorize,
            value.AmountMinor, value.Currency, PaymentCommandHandlers.ToAuthorizeResult);
    }

    private static PaymentCommand FromVoid(VoidPaymentAuthorizationV1 value, string operationKey)
    {
        RequirePayloadKeyEcho(value.OperationKey, operationKey);
        return new(
            value.TripId,
            operationKey,
            PaymentOperationType.Void,
            value.AmountMinor <= 0 ? 1 : value.AmountMinor,
            string.IsNullOrWhiteSpace(value.Currency) ? "VND" : value.Currency,
            PaymentCommandHandlers.ToVoidResult);
    }

    private static PaymentCommand FromCapture(CapturePaymentV1 value, string operationKey)
    {
        RequirePayloadKeyEcho(value.OperationKey, operationKey);
        return new(
            value.TripId,
            operationKey,
            PaymentOperationType.Capture,
            value.AmountMinor,
            value.Currency,
            PaymentCommandHandlers.ToCaptureResult);
    }

    private static void RequirePayloadKeyEcho(string? supplied, string operationKey)
    {
        if (supplied is not null && !string.Equals(supplied, operationKey, StringComparison.Ordinal))
            throw new ArgumentException("Payload operationKey must match the envelope.");
    }

    private static void ValidateIdentity(MessageEnvelope<JsonElement> envelope, Guid tripId)
    {
        if (envelope.AggregateId is not null
            && !string.Equals(envelope.AggregateId, tripId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Envelope and payload trip identity must match.");
    }

    private static (string MessageType, string RoutingKey) Describe(object outcome) => outcome switch
    {
        PaymentAuthorizedV1 => ("PaymentAuthorizedV1", "payment.authorized.v1"),
        PaymentAuthorizationFailedV1 => ("PaymentAuthorizationFailedV1", "payment.authorization-failed.v1"),
        PaymentAuthorizationVoidedV1 => ("PaymentAuthorizationVoidedV1", "payment.authorization-voided.v1"),
        PaymentCapturedV1 => ("PaymentCapturedV1", "payment.captured.v1"),
        PaymentCapturePendingV1 => ("PaymentCapturePendingV1", "payment.capture-pending.v1"),
        PaymentRequiresReviewV1 => ("PaymentRequiresReviewV1", "payment.requires-review.v1"),
        _ => throw new InvalidOperationException($"Unsupported Payment result {outcome.GetType().Name}."),
    };

    private sealed record PaymentCommand(
        Guid TripId,
        string OperationKey,
        PaymentOperationType Type,
        long AmountMinor,
        string Currency,
        Func<PaymentOperation, object> ToOutcome);
}
