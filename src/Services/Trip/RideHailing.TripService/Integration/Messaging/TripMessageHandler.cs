using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>One fresh scope per delivery; receipt and domain effect commit before ACK.</summary>
public sealed class TripMessageHandler(TripDbContext db, TimeProvider time, ILogger<TripMessageHandler> log)
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Subscriptions =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["trip.driver-state.q"] = new HashSet<string>(StringComparer.Ordinal)
            { "DriverReservedV1", "DriverReservationReleasedV1", "DriverTripStartedV1", "DriverTripCompletedV1" },
            ["trip.matching-outcome.q"] = new HashSet<string>(StringComparer.Ordinal) { "MatchingFailedV1" },
            ["trip.payment-outcome.q"] = new HashSet<string>(StringComparer.Ordinal)
            { "PaymentAuthorizedV1", "PaymentAuthorizationFailedV1", "PaymentAuthorizationVoidedV1",
              "PaymentCapturedV1", "PaymentCapturePendingV1", "PaymentRequiresReviewV1" }
        };

    public async Task HandleAsync(string consumer, MessageEnvelope<JsonElement> envelope, CancellationToken token)
    {
        Guid tripId;
        string json;
        string fingerprint;
        try
        {
            envelope.Validate();
            if (!Subscriptions.TryGetValue(consumer, out var types) || !types.Contains(envelope.MessageType) ||
                envelope.MessageId.Length > 256 || envelope.CorrelationId.Length > 256)
                throw new ArgumentException("Unsupported envelope");
            tripId = RequiredGuid(envelope.Payload, "tripId");
            json = JsonSerializer.Serialize(envelope);
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
            fingerprint = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        { throw new DeliveryFailureException(FailureKind.Poison, "INVALID_TRIP_MESSAGE"); }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var now = time.GetUtcNow();
            // Unique insert serializes duplicate deliveries across processes.
            // A rolled-back business change also rolls back the receipt claim.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO trip_message_receipts
                    (consumer, message_id, fingerprint, trip_id, message_type, correlation_id,
                     envelope_json, disposition, reason, recorded_at)
                VALUES ({consumer}, {envelope.MessageId}, {fingerprint}, {tripId},
                    {envelope.MessageType}, {envelope.CorrelationId}, CAST({json} AS jsonb),
                    'Processing', '', {now})
                ON CONFLICT (consumer, message_id) DO NOTHING
                """, token);
            var receipt = await db.MessageReceipts.SingleAsync(
                x => x.Consumer == consumer && x.MessageId == envelope.MessageId, token);
            if (inserted == 0)
            {
                if (receipt.Fingerprint != fingerprint)
                    throw new DeliveryFailureException(FailureKind.Poison, "MESSAGE_ID_CONTENT_CONFLICT");
                await transaction.CommitAsync(token);
                log.LogInformation("Trip message replay {MessageId} correlation {CorrelationId} result {Disposition}",
                    envelope.MessageId, envelope.CorrelationId, receipt.Disposition);
                return;
            }

            var trip = await db.Trips.SingleOrDefaultAsync(x => x.TripId == tripId, token);
            if (trip is null)
            {
                // May be out of order. Canonical bounded retries eventually transfer
                // to the error queue. Do not permanently consume a not-yet-known Trip.
                throw new DeliveryFailureException(FailureKind.Transient, "TRIP_NOT_FOUND");
            }
            Apply(trip, envelope, receipt, now);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            log.LogInformation("Trip message {MessageId} correlation {CorrelationId} trip {TripId} result {Disposition} reason {Reason}",
                envelope.MessageId, envelope.CorrelationId, tripId, receipt.Disposition, receipt.Reason);
        }
        catch (TripConcurrencyConflictException)
        { throw new DeliveryFailureException(FailureKind.Transient, "TRIP_CONCURRENCY_CONFLICT"); }
        catch (Exception ex) when (ex is NpgsqlException { IsTransient: true } ||
            ex is DbUpdateException { InnerException: NpgsqlException { IsTransient: true } })
        {
            // Only local DB effects: uncertain commit is safe to redeliver because
            // the durable receipt and effect are indivisible. No remote call here.
            throw new DeliveryFailureException(FailureKind.Transient, "TRIP_DATABASE_TRANSIENT");
        }
    }

    private static void Apply(Trip trip, MessageEnvelope<JsonElement> e, TripMessageReceipt receipt, DateTimeOffset now)
    {
        TripOutcome outcome;
        try
        {
            if (e.MessageType.StartsWith("Payment", StringComparison.Ordinal))
            {
                // W8 skeleton: preserve authority evidence until Saga can verify
                // operation lineage. A tripId alone never authorizes READY_TO_START.
                if (e.MessageType == "PaymentAuthorizedV1" && trip.State == TripState.Cancelled)
                    trip.RecordCancelledPaymentAuthorization(now);
                receipt.Disposition = "PendingWorkflow";
                receipt.Reason = "PAYMENT_LINEAGE_UNRESOLVED";
                return;
            }
            if (e.MessageType == "MatchingFailedV1")
                outcome = trip.MarkMatchingFailed(RequiredRound(e.Payload), now);
            else
            {
                var driverId = RequiredGuid(e.Payload, "driverId");
                var reservationId = RequiredGuid(e.Payload, "reservationId");
                if (e.MessageType == "DriverReservedV1")
                {
                    // B-W8 currently omits matchingRound. Keep evidence for review,
                    // never guess a round from mutable Trip state.
                    if (!e.Payload.TryGetProperty("matchingRound", out _))
                    {
                        receipt.Disposition = "NeedsReview";
                        receipt.Reason = "RESERVATION_ROUND_REQUIRED";
                        return;
                    }
                    var round = RequiredRound(e.Payload);
                    outcome = trip.State == TripState.Cancelled
                        ? trip.RecordCancelledReservation(driverId, reservationId, round, now)
                        : trip.ApplyDriverReserved(driverId, reservationId, round, now);
                }
                else if (e.MessageType == "DriverReservationReleasedV1")
                {
                    var intent = trip.CleanupIntents.SingleOrDefault(i =>
                        i.Kind == TripCleanupKind.ReleaseReservation && i.DriverId == driverId &&
                        i.ReservationId == reservationId);
                    outcome = intent is null
                        ? TripOutcome.Fail(TripErrorCode.StaleIdentity, "Release does not identify owned cleanup.")
                        : trip.ConfirmCleanup(intent.Key, now);
                }
                else
                    outcome = e.MessageType == "DriverTripStartedV1"
                        ? trip.Start(driverId, reservationId, now)
                        : trip.Complete(driverId, reservationId, now);
            }
        }
        catch (ArgumentException)
        { throw new DeliveryFailureException(FailureKind.Poison, "INVALID_TRIP_PAYLOAD"); }
        // A fresh messageId is not permission to bypass domain state/identity guards.
        // Retain rejected facts durably for reconciliation, without mutating Trip.
        receipt.Disposition = outcome.IsSuccess ? (outcome.IsReplay ? "BusinessReplay" : "Applied") : "NeedsReview";
        receipt.Reason = outcome.IsSuccess ? "" : outcome.ErrorCode.ToString();
    }

    private static Guid RequiredGuid(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty)
            throw new ArgumentException("Missing exact identity");
        return id;
    }

    private static int RequiredRound(JsonElement payload)
    {
        if (!payload.TryGetProperty("matchingRound", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var round) || round < 1)
            throw new ArgumentException("Missing positive matchingRound");
        return round;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate JSON property");
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
