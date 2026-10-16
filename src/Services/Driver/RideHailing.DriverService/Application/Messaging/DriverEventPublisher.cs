// B-W08-05: Driver authoritative event publisher (Outbox-CAPABLE, not
// Outbox-atomic — the dual-write gap is explicit and closes in W9).
// Best-effort by contract: publishes AFTER the DB commit; failure never
// fails the already-committed domain operation (failing a successful reserve
// over a telemetry-shaped event would fabricate failure). Every publish uses
// publisher confirms (mandatory route), the shared envelope + trace headers,
// and a deterministic per-effect messageId so retries/replays/recovery
// republish the SAME identity. Returns false (logged) instead of throwing.
// Long-lived serialized channel (same shape as the platform publisher
// contract); interim B-owned glue converging onto C's publisher base.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Topology;

namespace RideHailing.DriverService.Application.Messaging;

public sealed class DriverEventPublisher : IAsyncDisposable
{
    public static readonly ActivitySource Activities = new("RideHailing.DriverService");

    private readonly DriverBrokerOptions _options;
    private readonly ILogger<DriverEventPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public DriverEventPublisher(IOptions<DriverBrokerOptions> options, ILogger<DriverEventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> PublishStateAsync(
        string messageType,
        string routingKey,
        DriverStateEventPayload payload,
        int version,
        Guid driverId,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        var messageId = DriverEventMessageIds.ForDriverEffect(
            driverId, payload.ReservationId, version, payload.Status);
        var envelope = MessageEnvelope<DriverStateEventPayload>.Create(
            messageType,
            correlationId ?? Guid.NewGuid().ToString("N"),
            payload,
            aggregateType: "driver",
            aggregateId: driverId.ToString("N"),
            aggregateVersion: version,
            messageId: messageId);
        return await PublishAsync(DriverEventRoutes.EventsExchange, routingKey, envelope, ct);
    }

    public async Task<bool> PublishLocationAsync(
        DriverLocationEventPayload payload,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        var envelope = MessageEnvelope<DriverLocationEventPayload>.Create(
            DriverEventTypes.LocationAccepted,
            correlationId ?? Guid.NewGuid().ToString("N"),
            payload,
            aggregateType: "driver",
            aggregateId: payload.DriverId.ToString("N"),
            messageId: DriverEventMessageIds.ForLocationSample(
                payload.DriverId, payload.SessionId, payload.SequenceNo));
        return await PublishAsync(DriverEventRoutes.EventsExchange, DriverEventRoutes.LocationAccepted, envelope, ct);
    }

    internal async Task<bool> PublishAsync<T>(
        string exchange,
        string routingKey,
        MessageEnvelope<T> envelope,
        CancellationToken ct)
    {
        if (!_options.PublishingEnabled)
        {
            return false;
        }

        using var activity = Activities.StartActivity("publish", ActivityKind.Producer);
        activity?.SetTag("messaging.message.id", envelope.MessageId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PublishTimeoutSeconds));
        var acquired = false;
        try
        {
            await _gate.WaitAsync(timeout.Token);
            acquired = true;
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DriverEventPublisher));
            }
            var channel = await OpenChannelAsync(timeout.Token);
            var properties = MessageProperties.Create(
                envelope, activity?.Context ?? Activity.Current?.Context ?? default);
            await channel.BasicPublishAsync(
                exchange, routingKey, mandatory: true, properties,
                JsonSerializer.SerializeToUtf8Bytes(envelope), timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Best-effort: the domain commit already happened; losing the
            // event is the documented W8 dual-write residual (W9 Outbox).
            // Caller cancellation still propagates (no conclusive claim).
            _logger.LogWarning(
                ex, "Driver event publish failed (type={Type}, route={Route}); domain effect stands.",
                envelope.MessageType, routingKey);
            if (acquired) await ResetAsync();
            return false;
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    private async Task<IChannel> OpenChannelAsync(CancellationToken ct)
    {
        if (_connection is null || !_connection.IsOpen)
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            _channel = null;
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.Username,
                Password = _options.Password,
                ClientProvidedName = "driver-service/event-publisher",
                AutomaticRecoveryEnabled = false,
            };
            _connection = await factory.CreateConnectionAsync(ct);
            await RideTopology.DeclareAsync(_connection, ct);
        }

        if (_channel is null || !_channel.IsOpen)
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                ct);
        }

        return _channel;
    }

    private async Task ResetAsync()
    {
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }
        }
        catch
        {
        }

        _channel = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
