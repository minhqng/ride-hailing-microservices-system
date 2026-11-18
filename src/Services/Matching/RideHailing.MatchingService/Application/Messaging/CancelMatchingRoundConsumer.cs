// CancelMatchingRoundV1 broker consumer (matching.commands.q).
// Manual ACK after the durable cancel intent/flag commit; redelivery is
// idempotent. A cancel that precedes its RideRequestedV1 remains a durable
// tombstone that the intake consumes.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed class CancelMatchingRoundConsumer : BackgroundService
{
    private readonly ConnectionFactory _factory;
    private readonly CancelMatchingRoundHandler _handler;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<CancelMatchingRoundConsumer> _logger;

    public CancelMatchingRoundConsumer(
        ConnectionFactory factory,
        CancelMatchingRoundHandler handler,
        IOptions<RabbitMqOptions> options,
        ILogger<CancelMatchingRoundConsumer> logger)
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
            _logger.LogInformation("CancelMatchingRound consumer disabled by configuration.");
            return Task.CompletedTask;
        }

        return BrokerConsume.RunAsync(
            _factory,
            _options.CancelMatchingQueue,
            _options.Prefetch,
            _options.MaxRedeliveries,
            _handler.HandleAsync,
            _logger,
            stoppingToken);
    }
}
