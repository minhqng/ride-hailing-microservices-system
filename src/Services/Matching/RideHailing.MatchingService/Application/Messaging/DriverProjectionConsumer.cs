// B-W08-03/04: Driver state/location broker consumer (BackgroundService).
// Subscribes matching.driver-projection.q with manual ACK; each delivery
// runs DriverProjectionHandler (derived Redis update) to completion before
// ACK. Redis stays derived: Degraded results requeue boundedly and heal via
// redelivery or later authoritative events, never fabricate.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed class DriverProjectionConsumer : BackgroundService
{
    private readonly ConnectionFactory _factory;
    private readonly DriverProjectionHandler _handler;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<DriverProjectionConsumer> _logger;

    public DriverProjectionConsumer(
        ConnectionFactory factory,
        DriverProjectionHandler handler,
        IOptions<RabbitMqOptions> options,
        ILogger<DriverProjectionConsumer> logger)
    {
        _factory = factory;
        _handler = handler;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ConsumersEnabled)
        {
            _logger.LogInformation("Driver projection consumer disabled by configuration.");
            return Task.CompletedTask;
        }

        return BrokerConsume.RunAsync(
            _factory,
            _options.DriverProjectionQueue,
            _options.Prefetch,
            _options.MaxRedeliveries,
            _handler.HandleAsync,
            _logger,
            stoppingToken);
    }
}
