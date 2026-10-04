using RabbitMQ.Client;

namespace RideHailing.Messaging.Topology;

public static class RideTopology
{
    public const string CommandsExchange = "ride.commands";
    public const string EventsExchange = "ride.events";

    private static readonly (string Queue, string Exchange, string RoutingKey)[] Bindings =
    [
        ("matching.ride-requested.q", EventsExchange, "trip.ride-requested.v1"),
        ("trip.driver-state.q", EventsExchange, "driver.reserved.v1"),
        ("trip.driver-state.q", EventsExchange, "driver.reservation-released.v1"),
        ("trip.driver-state.q", EventsExchange, "driver.trip-started.v1"),
        ("trip.driver-state.q", EventsExchange, "driver.trip-completed.v1"),
        ("trip.matching-outcome.q", EventsExchange, "matching.failed.v1"),
        ("trip.payment-outcome.q", EventsExchange, "payment.authorized.v1"),
        ("trip.payment-outcome.q", EventsExchange, "payment.authorization-failed.v1"),
        ("trip.payment-outcome.q", EventsExchange, "payment.authorization-voided.v1"),
        ("trip.payment-outcome.q", EventsExchange, "payment.captured.v1"),
        ("trip.payment-outcome.q", EventsExchange, "payment.capture-pending.v1"),
        ("trip.payment-outcome.q", EventsExchange, "payment.requires-review.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.available.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.offline.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.reserved.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.reservation-released.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.trip-started.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.trip-completed.v1"),
        ("matching.driver-projection.q", EventsExchange, "driver.location-accepted.v1"),
        ("payment.commands.q", CommandsExchange, "payment.authorize.v1"),
        ("payment.commands.q", CommandsExchange, "payment.capture.v1"),
        ("payment.commands.q", CommandsExchange, "payment.void-authorization.v1"),
        ("driver.commands.q", CommandsExchange, "driver.release-reservation.v1"),
        ("driver.commands.q", CommandsExchange, "driver.mark-trip-started.v1"),
        ("driver.commands.q", CommandsExchange, "driver.mark-trip-completed.v1"),
        ("matching.commands.q", CommandsExchange, "matching.cancel-round.v1"),
        ("notification.business-events.q", EventsExchange, "trip.#"),
        ("notification.business-events.q", EventsExchange, "driver.#"),
        ("notification.business-events.q", EventsExchange, "payment.#"),
        ("notification.business-events.q", EventsExchange, "matching.#")
    ];

    public static async Task DeclareAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(CommandsExchange, ExchangeType.Topic, durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(EventsExchange, ExchangeType.Topic, durable: true,
            autoDelete: false, cancellationToken: cancellationToken);

        foreach (var (queue, exchange, routingKey) in Bindings)
        {
            var arguments = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: arguments, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(queue, exchange, routingKey, cancellationToken: cancellationToken);
        }
    }
}
