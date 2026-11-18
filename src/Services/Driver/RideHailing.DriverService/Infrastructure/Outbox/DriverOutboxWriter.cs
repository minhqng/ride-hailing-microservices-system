// Records the canonical Driver event as a durable Outbox intent
// using the shared NpgsqlOutboxStore. The caller MUST pass the same open
// connection/transaction that persists the authoritative Driver row; this
// type never opens its own connection and never talks to RabbitMQ.
using Npgsql;
using System.Text.Json;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;

namespace RideHailing.DriverService.Infrastructure.Outbox;

public sealed class DriverOutboxWriter(NpgsqlOutboxStore store)
{
    private readonly NpgsqlOutboxStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public Task EnqueueReservedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Driver driver, Guid tripId, Guid reservationId, CancellationToken ct = default) =>
        EnqueueAsync(connection, transaction, DriverEventMapper.ForReservedEffect(
            driver, tripId, reservationId), driver.UpdatedAt, ct);

    public Task EnqueueReleasedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Driver driver, Guid tripId, Guid reservationId, CancellationToken ct = default,
        MessageEnvelope<JsonElement>? command = null) =>
        EnqueueAsync(connection, transaction, DriverEventMapper.ForReleasedEffect(
            driver, tripId, reservationId), driver.UpdatedAt, ct, command);

    public Task EnqueueLifecycleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Driver driver,
        Guid tripId, Guid reservationId, bool completed, CancellationToken ct,
        MessageEnvelope<JsonElement>? command = null) =>
        EnqueueAsync(connection, transaction, completed
            ? DriverEventMapper.ForTripCompletedEffect(driver, tripId, reservationId)
            : DriverEventMapper.ForTripStartedEffect(driver, tripId, reservationId),
            driver.UpdatedAt, ct, command);

    public Task EnqueueAvailableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Driver driver, CancellationToken ct = default) =>
        EnqueueAsync(connection, transaction, DriverEventMapper.ForAvailableEffect(driver),
            driver.UpdatedAt, ct);

    /// <summary>
    /// Stages the Driver-addressed notification intent in the same
    /// transaction as the reservation/release effect. The recipient is the
    /// Driver aggregate's authoritative UserId, never the driverId.
    /// </summary>
    public async Task EnqueueNotificationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Driver driver, Guid tripId, Guid reservationId, string notificationType,
        CancellationToken ct = default)
    {
        if (tripId == Guid.Empty || reservationId == Guid.Empty)
            throw new ArgumentException("Notification requires exact trip/reservation identities.");
        var messageId = $"driver:{driver.DriverId:N}:notify:{notificationType.ToLowerInvariant()}:{tripId:N}:{reservationId:N}";
        var envelope = MessageEnvelope<NotificationRequestedV1>.Create(
            NotificationMessage.MessageType,
            correlationId: tripId.ToString("D"),
            payload: new NotificationRequestedV1
            {
                RecipientUserId = driver.UserId,
                RecipientRole = NotificationMessage.Roles.Driver,
                NotificationType = notificationType,
                TripId = tripId,
            },
            messageId: messageId,
            occurredAt: driver.UpdatedAt,
            aggregateType: "driver",
            aggregateId: driver.DriverId.ToString("N"),
            aggregateVersion: driver.Version,
            operationKey: messageId);
        var message = OutboxMessage.Create(
            envelope, DriverEventRoutes.EventsExchange, NotificationMessage.DriverRoutingKey);
        var inserted = await _store.EnqueueAsync(connection, transaction, message, ct);
        if (!inserted)
            throw new InvalidOperationException(
                $"Outbox notification intent '{messageId}' already exists for a new Driver effect.");
    }

    public Task EnqueueOfflineAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Driver driver, CancellationToken ct = default) =>
        EnqueueAsync(connection, transaction, DriverEventMapper.ForOfflineEffect(driver),
            driver.UpdatedAt, ct);

    private async Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        DriverEventToPublish effect, DateTimeOffset occurredAt, CancellationToken ct,
        MessageEnvelope<JsonElement>? command = null)
    {
        // The message id is derived once from the committed logical effect
        // (driver + reservation + version + end status). The relay stores and
        // reuses this id verbatim: a republish after an uncertain confirm never
        // mints a new identity. A new version/effect necessarily differs.
        var messageId = DriverEventMessageIds.ForDriverEffect(
            effect.Payload.DriverId, effect.Payload.ReservationId, effect.Version, effect.Payload.Status);

        var envelope = MessageEnvelope<DriverStateEventPayload>.Create(
            effect.MessageType,
            correlationId: command?.CorrelationId ?? Guid.NewGuid().ToString("N"),
            payload: effect.Payload,
            messageId: messageId,
            occurredAt: occurredAt,
            aggregateType: "driver",
            aggregateId: effect.Payload.DriverId.ToString("N"),
            aggregateVersion: effect.Version,
            causationId: command?.MessageId,
            sagaId: command?.SagaId,
            operationKey: command?.OperationKey);

        var message = OutboxMessage.Create(
            envelope, DriverEventRoutes.EventsExchange, effect.RoutingKey);

        var inserted = await _store.EnqueueAsync(connection, transaction, message, ct);
        if (!inserted)
        {
            // For a genuinely new committed effect this can only mean an
            // identity collision or a pre-existing intent. Either way the
            // caller's transaction must roll back rather than commit a state
            // change whose required event was not recorded as a new intent.
            throw new InvalidOperationException(
                $"Outbox intent '{messageId}' already exists for a new Driver effect.");
        }
    }
}
