// B-W08-01/02: RideRequestedV1 broker consumer (BackgroundService).
// Subscribes matching.ride-requested.q with manual ACK; each delivery runs
// RideRequestedHandler (durable round lookup/create) to completion before
// ACK. Crash-before-ACK redelivers; duplicate delivery converges by PK.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed class RideRequestedConsumer : BackgroundService
{
    private readonly ConnectionFactory _factory;
    private readonly RideRequestedHandler _handler;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RideRequestedConsumer> _logger;

    public RideRequestedConsumer(
        ConnectionFactory factory,
        RideRequestedHandler handler,
        IOptions<RabbitMqOptions> options,
        ILogger<RideRequestedConsumer> logger)
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
            _logger.LogInformation("RideRequested consumer disabled by configuration.");
            return Task.CompletedTask;
        }

        return BrokerConsume.RunAsync(
            _factory,
            _options.RideRequestedQueue,
            _options.Prefetch,
            _options.MaxRedeliveries,
            _handler.HandleAsync,
            _logger,
            stoppingToken);
    }
}
