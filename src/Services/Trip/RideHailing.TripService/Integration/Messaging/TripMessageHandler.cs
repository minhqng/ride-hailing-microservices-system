using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Observability;

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
              "PaymentAuthorizationVoidFailedV1", "PaymentCapturedV1", "PaymentCapturePendingV1",
              "PaymentRequiresReviewV1" }
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
            ValidateAuthorityMetadata(envelope);
            json = JsonSerializer.Serialize(envelope);
            fingerprint = SemanticFingerprint(envelope);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        { throw new DeliveryFailureException(FailureKind.Poison, "INVALID_TRIP_MESSAGE"); }

        // One domain-decision span per applied authority fact. It
        // nests under the shared "consume" transport span (different concern:
        // domain decision, not delivery) and, via Activity.Current at intent
        // creation, becomes the durable traceparent of every outgoing
        // saga/cleanup intent this transaction stages — so compensation and
        // capture dispatches stay correlated to the fact that caused them.
        using var span = TripDiagnostics.Activities.StartActivity("RideHailing.Trip/ApplyAuthority");
        span?.SetTag("messaging.message.id", envelope.MessageId);
        span?.SetTag("messaging.correlation.id", envelope.CorrelationId);
        span?.SetTag("messaging.message.type", envelope.MessageType);
        span?.SetTag("trip.id", tripId.ToString());

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
                // Compare semantic content, not diagnostic correlation or publish
                // timestamps. Read retained content also supports pre-hardening
                // receipts whose fingerprint covered the entire envelope.
                var original = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(receipt.EnvelopeJson)
                    ?? throw new InvalidOperationException("Stored message evidence is missing.");
                if (SemanticFingerprint(original) != fingerprint)
                    throw new DeliveryFailureException(FailureKind.Poison, "MESSAGE_ID_CONTENT_CONFLICT");
                await transaction.CommitAsync(token);
                // Redelivery replay: no business transition happened, so no
                // metric may claim one (replay semantics).
                span?.SetTag("ride.outcome", "receipt_replay");
                log.LogInformation("Trip message replay {MessageId} correlation {CorrelationId} result {Disposition}",
                    envelope.MessageId, envelope.CorrelationId, receipt.Disposition);
                return;
            }

            var trip = await db.Trips.SingleOrDefaultAsync(x => x.TripId == tripId, token);
            if (trip is null)
            {
                // The error envelope retains identity metadata, not the original
                // payload. Preserve out-of-order evidence locally for explicit
                // reconciliation instead of discarding it after bounded retries.
                receipt.Disposition = "PendingWorkflow";
                receipt.Reason = "TRIP_NOT_FOUND";
            }
            else
            {
                // A late authority fact may create an exact cleanup obligation
                // while the Trip stays terminal. The receipt, Trip effect, the
                // saga transition and any outgoing intent commit in this one
                // transaction.
                var saga = await db.RideSagas.SingleOrDefaultAsync(s => s.TripId == tripId, token);
                var preexistingCleanup = TripOutboxWriter.CleanupKeys(trip);
                var sagaStatusBefore = saga?.Status;
                var stateBefore = trip.State;
                Apply(trip, saga, envelope, receipt, now);
                EnqueueAppliedNotification(trip, envelope, receipt, stateBefore, now);
                TripOutboxWriter.EnqueueNewCleanup(db, trip, preexistingCleanup);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                // Post-commit observation only. The disposition was
                // decided inside the committed transaction; a rollback above
                // never reaches this point, so counters describe committed
                // effects, never handler invocations.
                ObserveCommittedOutcome(span, receipt, saga, sagaStatusBefore, trip);
                return;
            }

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            span?.SetTag("ride.outcome", "pending_workflow");
            log.LogWarning(
                "Trip message {MessageId} correlation {CorrelationId} references unknown trip {TripId}; parked for reconciliation.",
                envelope.MessageId, envelope.CorrelationId, tripId);
        }
        catch (TripConcurrencyConflictException)
        { throw new DeliveryFailureException(FailureKind.Transient, "TRIP_CONCURRENCY_CONFLICT"); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
            pg.ConstraintName is "PK_trip_cleanup_intents" or "outbox_messages_pk")
        {
            // Distinct deliveries can concurrently discover the same obligation.
            // Rollback and reload lets the domain compare the winner's identity;
            // never treat arbitrary unique violations as replay success.
            throw new DeliveryFailureException(FailureKind.Transient, "TRIP_CLEANUP_CONCURRENT");
        }
        catch (Exception ex) when (ex is NpgsqlException { IsTransient: true } ||
            ex is DbUpdateException { InnerException: NpgsqlException { IsTransient: true } })
        {
            // Only local DB effects: uncertain commit is safe to redeliver because
            // the durable receipt and effect are indivisible. No remote call here.
            throw new DeliveryFailureException(FailureKind.Transient, "TRIP_DATABASE_TRANSIENT");
        }
    }

    /// <summary>
    /// Post-commit observation: span tags, one structured log and
    /// counters that describe only durable effects. Receipt dispositions map
    /// to bounded outcome/reason vocabularies; saga counters fire once per
    /// committed NEW status entry, so redelivery (receipt replay) and domain
    /// replays (BusinessReplay) never inflate them.
    /// </summary>
    private void ObserveCommittedOutcome(
        Activity? span, TripMessageReceipt receipt, RideSaga? saga, RideSagaStatus? sagaStatusBefore, Trip trip)
    {
        span?.SetTag("trip.state", trip.State.ToCanonicalName());
        if (saga is not null)
        {
            span?.SetTag("saga.id", saga.SagaId.ToString());
            span?.SetTag("saga.step", saga.CurrentStep.ToCanonicalName());
        }

        span?.SetTag("ride.outcome", receipt.Disposition switch
        {
            "Applied" => "applied",
            "BusinessReplay" => "business_replay",
            "NeedsReview" => "needs_review",
            _ => receipt.Disposition,
        });

        if (receipt.Disposition == "NeedsReview")
        {
            TripDiagnostics.RecordTransitionRejected(RejectionReason(receipt.Reason));
            log.LogWarning(
                "Trip message {MessageId} correlation {CorrelationId} trip {TripId} rejected and parked for review: {Reason}.",
                receipt.MessageId, receipt.CorrelationId, trip.TripId, receipt.Reason);
        }
        else
        {
            log.LogInformation("Trip message {MessageId} correlation {CorrelationId} trip {TripId} result {Disposition} reason {Reason}",
                receipt.MessageId, receipt.CorrelationId, trip.TripId, receipt.Disposition, receipt.Reason);
        }

        if (saga is not null && sagaStatusBefore is { } before)
        {
            if (before != RideSagaStatus.Compensating && saga.Status == RideSagaStatus.Compensating)
                TripDiagnostics.RecordSagaCompensation();
            if (before != RideSagaStatus.RequiresReview && saga.Status == RideSagaStatus.RequiresReview)
                TripDiagnostics.RecordSagaRequiresReview();
        }
    }

    /// <summary>Maps a durable rejection reason to a bounded metric dimension.</summary>
    private static string RejectionReason(string reason) =>
        Enum.TryParse<TripErrorCode>(reason, out var code) && code != TripErrorCode.None
            ? SnakeCase(code.ToString())
            : TripDiagnostics.RejectionReasons.AuthorityRejected;

    private static string SnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (char.IsUpper(ch) && builder.Length > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private void Apply(Trip trip, RideSaga? saga, MessageEnvelope<JsonElement> e, TripMessageReceipt receipt, DateTimeOffset now)
    {
        if (e.MessageType.StartsWith("Payment", StringComparison.Ordinal))
        {
            // The outcome resolves the saga's awaited step only under the
            // frozen operationKey/sagaId lineage; a tripId alone never moves
            // ride state.
            var result = PaymentOutcomeIntake.Apply(trip, saga, PaymentFact(e), now, db);
            receipt.Disposition = result.Disposition;
            receipt.Reason = result.Reason;
            return;
        }

        TripOutcome outcome;
        try
        {
            if (e.MessageType == "MatchingFailedV1")
                outcome = trip.MarkMatchingFailed(RequiredRound(e.Payload), now);
            else
            {
                var driverId = RequiredGuid(e.Payload, "driverId");
                var reservationId = RequiredGuid(e.Payload, "reservationId");
                if (e.MessageType == "DriverReservedV1")
                {
                    // Driver carries matchingRound when it holds durable
                    // generation provenance for the reservation identity
                    // (lineage). Events may still omit it from historical
                    // rows; park those for review — never guess a round from
                    // mutable Trip state.
                    if (!e.Payload.TryGetProperty("matchingRound", out _))
                    {
                        receipt.Disposition = "NeedsReview";
                        receipt.Reason = "RESERVATION_ROUND_REQUIRED";
                        return;
                    }
                    var round = RequiredRound(e.Payload);
                    outcome = trip.State is TripState.Cancelled or TripState.MatchFailed
                        ? trip.RecordPreStartTerminalReservation(driverId, reservationId, round, now)
                        : trip.ApplyDriverReserved(driverId, reservationId, round, now);
                }
                else if (e.MessageType == "DriverReservationReleasedV1")
                {
                    var intent = trip.CleanupIntents.SingleOrDefault(i =>
                        i.Kind == TripCleanupKind.ReleaseReservation && i.DriverId == driverId &&
                        i.ReservationId == reservationId);
                    if (intent is null)
                    {
                        outcome = TripOutcome.Fail(TripErrorCode.StaleIdentity, "Release does not identify owned cleanup.");
                    }
                    else
                    {
                        outcome = trip.ConfirmCleanup(intent.Key, now);
                        // The exact release completes a compensation obligation:
                        // the saga finishes only when every obligation resolved.
                        if (outcome.IsSuccess && saga is { Status: RideSagaStatus.Compensating })
                        {
                            var released = saga.RecordReservationReleased(now);
                            if (!released.IsSuccess)
                            {
                                throw new InvalidOperationException(
                                    $"Saga rejected reservation-release completion: {released.ErrorCode} {released.Message}");
                            }
                        }
                    }
                }
                else if (e.MessageType == "DriverTripCompletedV1")
                {
                    if (trip.State == TripState.Completed)
                    {
                        // Command-response agreement: the completion was already
                        // recorded Trip-side; this authoritative confirmation is
                        // a durable replay, never a second capture effect.
                        receipt.Disposition = trip.Assignment is not null &&
                            trip.Assignment.DriverId == driverId &&
                            trip.Assignment.ReservationId == reservationId
                            ? "BusinessReplay"
                            : "NeedsReview";
                        receipt.Reason = receipt.Disposition == "BusinessReplay" ? string.Empty : "AssignmentIdentityMismatch";
                        return;
                    }

                    outcome = trip.Complete(driverId, reservationId, now);
                    if (outcome.IsSuccess && !outcome.IsReplay)
                    {
                        // The physical ride pivot happened: dispatch the capture
                        // obligation with the stable capture key.
                        // No MarkDriverTripCompleted command: Driver authority
                        // supplied this completion fact itself.
                        if (saga is not null)
                        {
                            var completed = saga.RecordRideCompleted(RideSagaOperationKeys.Capture(trip.TripId), now);
                            if (!completed.IsSuccess)
                            {
                                throw new InvalidOperationException(
                                    $"Saga rejected the completion step: {completed.ErrorCode} {completed.Message}");
                            }

                            TripOutboxWriter.Enqueue(db, TripSagaIntents.CapturePayment(trip, saga, now));
                        }
                    }
                }
                else
                    outcome = trip.Start(driverId, reservationId, now);
            }
        }
        catch (ArgumentException)
        { throw new DeliveryFailureException(FailureKind.Poison, "INVALID_TRIP_PAYLOAD"); }
        // A fresh messageId is not permission to bypass domain state/identity guards.
        // Retain rejected facts durably for reconciliation, without mutating Trip.
        receipt.Disposition = outcome.IsSuccess ? (outcome.IsReplay ? "BusinessReplay" : "Applied") : "NeedsReview";
        receipt.Reason = outcome.IsSuccess ? string.Empty : outcome.ErrorCode.ToString();
    }

    /// <summary>
    /// Stages the Rider-visible notification only for a FIRST applied
    /// business fact. Late/duplicate/terminal facts that are parked or replayed
    /// never produce a second notification; a late reservation that merely
    /// creates cleanup evidence is not an assignment event.
    /// </summary>
    private void EnqueueAppliedNotification(Trip trip, MessageEnvelope<JsonElement> envelope,
        TripMessageReceipt receipt, TripState stateBefore, DateTimeOffset now)
    {
        if (receipt.Disposition != "Applied") return;
        switch (envelope.MessageType)
        {
            case "DriverReservedV1" when trip.State == TripState.DriverReserved:
                TripOutboxWriter.Enqueue(db, TripNotificationIntents.DriverAssigned(trip, now));
                break;
            case "MatchingFailedV1" when trip.State == TripState.MatchFailed:
                TripOutboxWriter.Enqueue(db, TripNotificationIntents.NoDriver(trip, now));
                break;
            case "DriverTripCompletedV1" when trip.State == TripState.Completed:
                TripOutboxWriter.Enqueue(db, TripNotificationIntents.Completed(trip, now));
                break;
            case "PaymentAuthorizationFailedV1"
                when stateBefore == TripState.DriverAccepted && trip.State == TripState.Cancelled:
                TripOutboxWriter.Enqueue(db,
                    TripNotificationIntents.PaymentFailed(trip, "DECLINED", now));
                break;
        }
    }

    private static PaymentOutcomeFact PaymentFact(MessageEnvelope<JsonElement> envelope)
    {
        // Payment's authoritative failure/pending/review payloads carry the
        // provider reason as `reasonCode` (PaymentMessages.cs). Reading a
        // different field here silently turns a terminal DECLINED into an
        // uncertain outcome, so this name is part of the cross-service
        // contract and is pinned by PaymentMessageContractsTests.
        string? reasonCode = null;
        if (envelope.Payload.ValueKind == JsonValueKind.Object &&
            envelope.Payload.TryGetProperty("reasonCode", out var code) &&
            code.ValueKind == JsonValueKind.String)
        {
            reasonCode = code.GetString();
        }

        var retryable = false;
        if (envelope.Payload.ValueKind == JsonValueKind.Object &&
            envelope.Payload.TryGetProperty("retryable", out var flag) &&
            flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            retryable = flag.GetBoolean();
        }

        return new PaymentOutcomeFact(
            envelope.MessageType, envelope.OperationKey, envelope.SagaId, reasonCode, retryable);
    }

    private static Guid RequiredGuid(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty)
            throw new ArgumentException("Missing exact identity");
        return id;
    }

    private static void ValidateAuthorityMetadata(MessageEnvelope<JsonElement> envelope)
    {
        if (!envelope.MessageType.StartsWith("Driver", StringComparison.Ordinal)) return;
        var driverId = RequiredGuid(envelope.Payload, "driverId");
        _ = RequiredGuid(envelope.Payload, "reservationId");
        // Optional metadata may be absent in the minimal intake contract, but
        // supplied contradictory authority facts must never change business state.
        if (envelope.AggregateType is not null &&
            (!string.Equals(envelope.AggregateType, "Driver", StringComparison.OrdinalIgnoreCase) ||
             !Guid.TryParse(envelope.AggregateId, out var aggregateId) || aggregateId != driverId))
            throw new ArgumentException("Driver aggregate identity mismatch");
        if (envelope.Payload.TryGetProperty("status", out var status))
        {
            var expected = envelope.MessageType switch
            {
                "DriverReservedV1" => "RESERVED",
                "DriverTripStartedV1" => "ON_TRIP",
                _ => "AVAILABLE"
            };
            if (status.ValueKind != JsonValueKind.String || status.GetString() != expected)
                throw new ArgumentException("Driver event/state mismatch");
        }
        if (envelope.Payload.TryGetProperty("version", out var version) &&
            (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var value) || value < 0 ||
             (envelope.AggregateVersion.HasValue && value != envelope.AggregateVersion.Value)))
            throw new ArgumentException("Driver version mismatch");
    }

    private static int RequiredRound(JsonElement payload)
    {
        if (!payload.TryGetProperty("matchingRound", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var round) || round < 1)
            throw new ArgumentException("Missing positive matchingRound");
        return round;
    }

    private static string SemanticFingerprint(MessageEnvelope<JsonElement> envelope)
    {
        var semantic = JsonSerializer.SerializeToElement(new
        {
            envelope.MessageType, envelope.SchemaVersion, envelope.Payload,
            envelope.AggregateType, envelope.AggregateId, envelope.AggregateVersion,
            envelope.SagaId, envelope.OperationKey
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, semantic);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
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
