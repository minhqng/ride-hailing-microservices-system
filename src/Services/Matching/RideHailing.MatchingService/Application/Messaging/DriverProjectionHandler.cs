// B-W08-03/04: Driver state/location events → derived Redis projection.
// The W4 atomic function owns all guard semantics (version/freshness/GEO);
// this handler only maps broker events to updater intents. Degraded updater
// results become bounded redelivery (transient/degraded), never silent
// success: a degraded projection must heal via redelivery or later events.
// Unknown routing keys are poison (contract drift fails loud, not wedged).
// Event payloads are B-defined on both ends (Driver publishes, Matching
// consumes) and recorded in the DriverClient README; unknown extra fields
// are ignored by the tolerant reader.
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RideHailing.MatchingService.Application.Projection;

namespace RideHailing.MatchingService.Application.Messaging;

public sealed class DriverProjectionHandler
{
    private readonly RedisProjectionUpdater _updater;
    private readonly ILogger<DriverProjectionHandler> _logger;

    public DriverProjectionHandler(RedisProjectionUpdater updater, ILogger<DriverProjectionHandler> logger)
    {
        _updater = updater;
        _logger = logger;
    }

    public async Task<HandlerOutcome> HandleAsync(BrokerDelivery delivery, CancellationToken ct)
    {
        var envelope = BrokerEnvelope.Parse(delivery.Body);
        var (expectedType, expectedStatus) = delivery.RoutingKey switch
        {
            "driver.available.v1" => ("DriverAvailableV1", "AVAILABLE"),
            "driver.offline.v1" => ("DriverOfflineV1", "OFFLINE"),
            "driver.reserved.v1" => ("DriverReservedV1", "RESERVED"),
            "driver.reservation-released.v1" => ("DriverReservationReleasedV1", "AVAILABLE"),
            "driver.trip-started.v1" => ("DriverTripStartedV1", "ON_TRIP"),
            "driver.trip-completed.v1" => ("DriverTripCompletedV1", "AVAILABLE"),
            "driver.location-accepted.v1" => ("DriverLocationAcceptedV1", (string?)null),
            _ => throw new ArgumentException("Unsupported Driver routing key.")
        };
        if (envelope.MessageType != expectedType)
            throw new ArgumentException("Driver message type does not match routing key.");
        if (expectedStatus is not null && ReadState(envelope.Payload).DerivedStatus != expectedStatus)
            throw new ArgumentException("Driver state does not match event type.");
        ProjectionResult result = delivery.RoutingKey switch
        {
            "driver.available.v1" or "driver.offline.v1" or "driver.reserved.v1"
                or "driver.reservation-released.v1" or "driver.trip-started.v1" or "driver.trip-completed.v1"
                => await _updater.ApplyStateAsync(ReadState(envelope.Payload)),
            "driver.location-accepted.v1"
                => await _updater.ApplyLocationAsync(ReadLocation(envelope.Payload)),
            _ => throw new ArgumentException($"Unsupported routing key '{delivery.RoutingKey}'."),
        };

        if (result.Degraded)
        {
            return HandlerOutcome.Retry;
        }

        _logger.LogDebug(
            "Projected {RoutingKey}: state={State} location={Location} geo={Geo}.",
            delivery.RoutingKey, result.State, result.Location, result.GeoMember);
        return HandlerOutcome.Done;
    }

    internal static DriverStateProjectionUpdate ReadState(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Driver state event payload must be a JSON object.");
        }

        if (!payload.TryGetProperty("driverId", out var idEl) || idEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idEl.GetString(), out var driverId) || driverId == Guid.Empty)
        {
            throw new ArgumentException("Driver state event requires a non-empty driverId.");
        }

        if (!payload.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(statusEl.GetString()))
        {
            throw new ArgumentException("Driver state event requires a status.");
        }

        if (!payload.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Number
            || !versionEl.TryGetInt64(out var version) || version < 0)
        {
            throw new ArgumentException("Driver state event requires a non-negative version.");
        }

        return new DriverStateProjectionUpdate(driverId, statusEl.GetString()!, version);
    }

    internal static LocationProjectionUpdate ReadLocation(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Driver location event payload must be a JSON object.");
        }

        if (!payload.TryGetProperty("driverId", out var idEl) || idEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idEl.GetString(), out var driverId) || driverId == Guid.Empty)
        {
            throw new ArgumentException("Driver location event requires a non-empty driverId.");
        }

        if (!payload.TryGetProperty("longitude", out var lonEl) || lonEl.ValueKind != JsonValueKind.Number
            || !payload.TryGetProperty("latitude", out var latEl) || latEl.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException("Driver location event requires numeric longitude/latitude.");
        }

        if (!payload.TryGetProperty("sessionId", out var sessionEl) || sessionEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sessionEl.GetString()))
        {
            throw new ArgumentException("Driver location event requires a sessionId.");
        }

        if (!payload.TryGetProperty("sequenceNo", out var seqEl) || seqEl.ValueKind != JsonValueKind.Number
            || !seqEl.TryGetInt64(out var sequenceNo) || sequenceNo < 0)
        {
            throw new ArgumentException("Driver location event requires a non-negative sequenceNo.");
        }

        if (!payload.TryGetProperty("capturedAt", out var tsEl) || tsEl.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(tsEl.GetString(), out var capturedAt))
        {
            throw new ArgumentException("Driver location event requires a parseable capturedAt.");
        }

        return new LocationProjectionUpdate(
            driverId,
            lonEl.GetDouble(),
            latEl.GetDouble(),
            sessionEl.GetString()!,
            sequenceNo,
            capturedAt);
    }
}
