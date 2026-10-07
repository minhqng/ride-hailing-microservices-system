// B-W08 failure disposition for consumed messages (Doc 02 §16.2 taxonomy,
// interim until C's DLQ lands). Pure functions, directly unit-tested:
//   - validation/domain/permanent/bug → Drop (poison must never hot-loop;
//     DLQ/error-store arrival is W9/C scope, error is logged with identity).
//   - transient infrastructure (PG down, socket, timeout) → Requeue.
//   - caller cancellation → Requeue (redeliver later; never ACK away work).
// Bounded by the broker acquisition count: RabbitMQ 4.x quorum queues
// expose x-acquired-count (legacy x-delivery-count honored); past
// MaxRedeliveries anything becomes Drop (fail loud in logs, never infinite).
// A headerless redelivery carries no acquisition evidence and fails closed
// (drop). Unknown exceptions are treated as transient ONLY within budget;
// the bound is what prevents a poison hot loop.
namespace RideHailing.MatchingService.Application.Messaging;

public enum ConsumeDisposition
{
    Requeue,
    Drop,
}

public static class BrokerDecisions
{
    /// <summary>Maximum accepted broker message body (1 MiB), mirroring the
    /// platform publish limit. Larger bodies are poison (drop, never parse):
    /// parsing unbounded input risks memory exhaustion from an untrusted
    /// producer path.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    public static bool BodyTooLarge(int byteCount) => byteCount > MaxBodyBytes;

    public static ConsumeDisposition ClassifyFailure(Exception ex) => ex switch
    {
        OperationCanceledException => ConsumeDisposition.Requeue,
        Npgsql.NpgsqlException => ConsumeDisposition.Requeue,
        System.Net.Sockets.SocketException => ConsumeDisposition.Requeue,
        TimeoutException => ConsumeDisposition.Requeue,
        _ => ConsumeDisposition.Drop,
    };

    public static int DeliveryCount(IDictionary<string, object?>? headers, bool redelivered)
    {
        // RabbitMQ 4.x quorum queues expose x-acquired-count (Int64) on
        // redelivery: first redelivery carries 1, so sightings = value + 1.
        // Legacy x-delivery-count is honored the same way. Any other numeric
        // encoding is converted defensively; a non-numeric value is ignored.
        // Headerless redelivery (classic queue or stripped headers) carries
        // no acquisition evidence: fail closed with int.MaxValue so the
        // budget check drops instead of hot-looping unbounded.
        if (headers is not null)
        {
            if (TryAcquireCount(headers, "x-acquired-count", out var acquired)
                || TryAcquireCount(headers, "x-delivery-count", out acquired))
            {
                return (int)Math.Min(int.MaxValue - 1L, acquired + 1);
            }
        }

        if (redelivered)
        {
            return int.MaxValue;
        }

        return 1;
    }

    private static bool TryAcquireCount(
        IDictionary<string, object?> headers, string key, out long value)
    {
        value = 0;
        if (!headers.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        try
        {
            value = raw switch
            {
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                uint u => u,
                ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
                ushort us => us,
                _ => Convert.ToInt64(raw),
            };
        }
        catch (Exception)
        {
            return false;
        }

        return value >= 0;
    }

    public static bool ExceedsBudget(int deliveryCount, int maxRedeliveries) =>
        deliveryCount > maxRedeliveries;
}
