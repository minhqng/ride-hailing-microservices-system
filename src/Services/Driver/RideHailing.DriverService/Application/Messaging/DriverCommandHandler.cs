using System.Text.Json;
using Npgsql;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;

namespace RideHailing.DriverService.Application.Messaging;

public sealed class DriverCommandHandler(DriverRepository repository)
{
    public async Task HandleAsync(MessageEnvelope<JsonElement> envelope, CancellationToken ct)
    {
        try { await repository.HandleCommandAsync(envelope, ct); }
        catch (ArgumentException) { throw new DeliveryFailureException(FailureKind.Poison, "INVALID_DRIVER_COMMAND"); }
        catch (NpgsqlException ex) when (ex.IsTransient)
        { throw new DeliveryFailureException(FailureKind.Transient, "DRIVER_DATABASE_UNAVAILABLE"); }
        catch (TimeoutException)
        { throw new DeliveryFailureException(FailureKind.Transient, "DRIVER_DATABASE_TIMEOUT"); }
    }
}
