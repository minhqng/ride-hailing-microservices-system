using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Dedup;
using RideHailing.Messaging.Outbox;
using RideHailing.PaymentService.Infrastructure;
using RideHailing.PaymentService.Integration.Payment;

namespace RideHailing.PaymentService.IntegrationTests;

public class PaymentMessagingTransactionTests(PaymentDatabase database) : IClassFixture<PaymentDatabase>
{
    [Fact]
    public async Task RedeliveryKeepsOnePaymentEffectMarkerAndResultIntent()
    {
        var processed = new ProcessedMessageStore(database.Source);
        var outbox = new NpgsqlOutboxStore(database.Source);
        await processed.EnsureSchemaAsync();
        await outbox.EnsureSchemaAsync();
        await using var provider = new ProviderLedger(database.ConnectionString);
        var processor = new PaymentMessageProcessor(
            new PaymentExecution(database.Source, new PaymentSimulator(provider)),
            processed,
            outbox,
            TimeProvider.System,
            NullLogger<PaymentMessageProcessor>.Instance);

        var tripId = Guid.NewGuid();
        var operationKey = $"{tripId:D}:authorize:v1";
        var command = new AuthorizePaymentV1
        {
            TripId = tripId,
            OperationKey = operationKey,
            AmountMinor = 25_000,
            Currency = "VND",
        };
        var envelope = MessageEnvelope<JsonElement>.Create(
            "AuthorizePaymentV1",
            "payment-message-transaction-test",
            JsonSerializer.SerializeToElement(command),
            messageId: $"msg-{Guid.NewGuid():N}",
            aggregateType: "Trip",
            aggregateId: tripId.ToString("D"),
            sagaId: $"saga-{tripId:N}",
            operationKey: operationKey);

        await processor.ProcessAsync(envelope, CancellationToken.None);
        await processor.ProcessAsync(envelope, CancellationToken.None);
        var producerRetry = envelope with { MessageId = $"msg-{Guid.NewGuid():N}" };
        await processor.ProcessAsync(producerRetry, CancellationToken.None);

        await using var count = database.Source.CreateCommand("""
            SELECT
              (SELECT count(*) FROM payment_operations WHERE operation_key=@key),
              (SELECT count(*) FROM provider_effects WHERE operation_key=@key),
              (SELECT count(*) FROM processed_messages WHERE consumer_scope=@scope AND message_id IN (@message,@retry_message)),
              (SELECT count(*) FROM outbox_messages WHERE message_id=@result_message)
            """);
        count.Parameters.AddWithValue("key", operationKey);
        count.Parameters.AddWithValue("scope", PaymentMessageProcessor.ConsumerScope);
        count.Parameters.AddWithValue("message", envelope.MessageId);
        count.Parameters.AddWithValue("retry_message", producerRetry.MessageId);
        var operationId = await FindOperationIdAsync(operationKey);
        var resultMessageId = $"payment:{operationId:N}:PaymentAuthorizedV1";
        count.Parameters.AddWithValue("result_message", resultMessageId);
        await using var row = await count.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1, row.GetInt64(0));
        Assert.Equal(1, row.GetInt64(1));
        Assert.Equal(2, row.GetInt64(2));
        Assert.Equal(1, row.GetInt64(3));

        var result = await outbox.LoadAsync(resultMessageId);
        Assert.NotNull(result);
        Assert.Equal("PaymentAuthorizedV1", result!.MessageType);
        Assert.Equal("payment.authorized.v1", result.RoutingKey);
        Assert.Equal(operationKey, result.OperationKey);
    }

    [Fact]
    public async Task TripShapedPayloadWithoutOperationKeyUsesTheEnvelopeIdentity()
    {
        var processed = new ProcessedMessageStore(database.Source);
        var outbox = new NpgsqlOutboxStore(database.Source);
        await processed.EnsureSchemaAsync();
        await outbox.EnsureSchemaAsync();
        await using var provider = new ProviderLedger(database.ConnectionString);
        var processor = new PaymentMessageProcessor(
            new PaymentExecution(database.Source, new PaymentSimulator(provider)),
            processed,
            outbox,
            TimeProvider.System,
            NullLogger<PaymentMessageProcessor>.Instance);

        var tripId = Guid.NewGuid();
        var operationKey = $"{tripId:D}:authorize:v1";
        // Exact Trip producer shape: operationKey/sagaId travel on the
        // envelope and are deliberately absent from the payload.
        var payload = JsonSerializer.SerializeToElement(new
        {
            tripId,
            amountMinor = 25_000,
            currency = "VND",
        });
        var envelope = MessageEnvelope<JsonElement>.Create(
            "AuthorizePaymentV1",
            "trip-shaped-payment-contract",
            payload,
            messageId: $"msg-{Guid.NewGuid():N}",
            aggregateType: "Trip",
            aggregateId: tripId.ToString("D"),
            sagaId: Guid.NewGuid().ToString("D"),
            operationKey: operationKey);

        await processor.ProcessAsync(envelope, CancellationToken.None);

        var operationId = await FindOperationIdAsync(operationKey);
        var result = await outbox.LoadAsync($"payment:{operationId:N}:PaymentAuthorizedV1");
        Assert.NotNull(result);
        Assert.Equal("PaymentAuthorizedV1", result!.MessageType);
        Assert.Equal(operationKey, result.OperationKey);
        Assert.Equal(tripId.ToString("D"), result.AggregateId);
        Assert.Equal(envelope.SagaId, result.SagaId);
    }

    private async Task<Guid> FindOperationIdAsync(string operationKey)
    {
        await using var command = database.Source.CreateCommand(
            "SELECT id FROM payment_operations WHERE operation_key=@key");
        command.Parameters.AddWithValue("key", operationKey);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}
