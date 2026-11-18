// Trip saga Outbox intents.
//
// One builder per committed saga obligation, following the TripOutboxIntents
// contract: a deterministic messageId derived from the
// exact business identity (never a fresh Guid per attempt), the payload
// serialized from committed values, and the frozen operationKey/sagaId
// lineage on the envelope.
//
// Message identities are stable per LOGICAL step, so the outbox primary key
// is a second fence against a duplicate logical payment effect: a retry or a
// racing duplicate that re-enqueues the same step hits the existing row and
// is reconciled by same-obligation discovery instead of creating a second
// command.

using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Matching;
using RideHailing.TripService.Integration.Payment;

namespace RideHailing.TripService.Integration.Messaging;

/// <summary>Builds Trip-owned durable saga intents from committed identities.</summary>
public static class TripSagaIntents
{
    public const string AuthorizePaymentType = "AuthorizePaymentV1";
    public const string CapturePaymentType = "CapturePaymentV1";
    public const string VoidPaymentAuthorizationType = "VoidPaymentAuthorizationV1";
    public const string MarkDriverTripStartedType = "MarkDriverTripStartedV1";
    public const string MarkDriverTripCompletedType = "MarkDriverTripCompletedV1";

    public const string AuthorizePaymentRoutingKey = "payment.authorize.v1";
    public const string CapturePaymentRoutingKey = "payment.capture.v1";
    public const string VoidPaymentAuthorizationRoutingKey = "payment.void-authorization.v1";
    public const string MarkDriverTripStartedRoutingKey = "driver.mark-trip-started.v1";
    public const string MarkDriverTripCompletedRoutingKey = "driver.mark-trip-completed.v1";

    /// <summary>Stable message identity for the ride's one logical authorize step.</summary>
    public static string AuthorizePaymentMessageId(Guid tripId) => $"trip:{tripId:N}:authorize:v1";

    /// <summary>Stable message identity for the ride's one logical capture step.</summary>
    public static string CapturePaymentMessageId(Guid tripId) => $"trip:{tripId:N}:capture:v1";

    /// <summary>Stable message identity for the ride's one logical void step.</summary>
    public static string VoidPaymentAuthorizationMessageId(Guid tripId) => $"trip:{tripId:N}:void:v1";

    /// <summary>Stable message identity for the ride's one start command.</summary>
    public static string MarkDriverTripStartedMessageId(Guid tripId, Guid reservationId) =>
        $"trip:{tripId:N}:mark-started:{reservationId:N}";

    /// <summary>Stable message identity for the ride's one completion command.</summary>
    public static string MarkDriverTripCompletedMessageId(Guid tripId, Guid reservationId) =>
        $"trip:{tripId:N}:mark-completed:{reservationId:N}";

    /// <summary>
    /// AuthorizePaymentV1 for the DriverAccepted→Authorize saga step. The
    /// caller persists the returned intent in the same SaveChanges as the
    /// Trip's DRIVER_ACCEPTED transition and the new saga row.
    /// </summary>
    public static OutboxMessage AuthorizePayment(Trip trip, RideSaga saga, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(saga);
        RequireAcceptedTrip(trip, nameof(AuthorizePayment));
        RequireSagaLineage(trip, saga, RideSagaStep.AuthorizePending, nameof(AuthorizePayment));
        ArgumentNullException.ThrowIfNull(saga.AuthorizeOperationKey);

        var amountMinor = PaymentAmounts.ToMinorUnits(trip.Fare);
        var envelope = MessageEnvelope<AuthorizePaymentV1>.Create(
            AuthorizePaymentType,
            trip.TripId.ToString("D"),
            new AuthorizePaymentV1
            {
                TripId = trip.TripId,
                OperationKey = saga.AuthorizeOperationKey,
                AmountMinor = amountMinor,
                Currency = trip.Fare.Currency,
            },
            messageId: AuthorizePaymentMessageId(trip.TripId),
            occurredAt: occurredAt,
            aggregateType: TripOutboxIntents.TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            sagaId: saga.SagaId.ToString("D"),
            operationKey: saga.AuthorizeOperationKey);
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            AuthorizePaymentRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// CapturePaymentV1 for the completion→capture saga step (only
    /// after the Trip reached COMPLETED). Same SaveChanges as the COMPLETED
    /// transition and the saga's CAPTURE_PENDING step.
    /// </summary>
    public static OutboxMessage CapturePayment(Trip trip, RideSaga saga, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(saga);
        if (trip.State != TripState.Completed)
        {
            throw new InvalidOperationException(
                $"CapturePayment requires a COMPLETED Trip but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }

        RequireSagaLineage(trip, saga, RideSagaStep.CapturePending, nameof(CapturePayment));
        ArgumentNullException.ThrowIfNull(saga.CaptureOperationKey);

        var amountMinor = PaymentAmounts.ToMinorUnits(trip.Fare);
        var envelope = MessageEnvelope<CapturePaymentV1>.Create(
            CapturePaymentType,
            trip.TripId.ToString("D"),
            new CapturePaymentV1
            {
                TripId = trip.TripId,
                OperationKey = saga.CaptureOperationKey,
                AmountMinor = amountMinor,
                Currency = trip.Fare.Currency,
            },
            messageId: CapturePaymentMessageId(trip.TripId),
            occurredAt: occurredAt,
            aggregateType: TripOutboxIntents.TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            sagaId: saga.SagaId.ToString("D"),
            operationKey: saga.CaptureOperationKey);
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            CapturePaymentRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// VoidPaymentAuthorizationV1 for the authorized-after-cancel compensation
    /// step: the authorization effect exists at Payment authority, so an exact
    /// void with the stable void key is dispatched; the Trip stays CANCELLED.
    /// </summary>
    public static OutboxMessage VoidPaymentAuthorization(Trip trip, RideSaga saga, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(saga);
        if (trip.State != TripState.Cancelled)
        {
            throw new InvalidOperationException(
                $"VoidPaymentAuthorization compensates a CANCELLED Trip but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }

        RequireSagaLineage(trip, saga, RideSagaStep.VoidPending, nameof(VoidPaymentAuthorization));
        ArgumentNullException.ThrowIfNull(saga.VoidOperationKey);

        var amountMinor = PaymentAmounts.ToMinorUnits(trip.Fare);
        var envelope = MessageEnvelope<VoidPaymentAuthorizationV1>.Create(
            VoidPaymentAuthorizationType,
            trip.TripId.ToString("D"),
            new VoidPaymentAuthorizationV1
            {
                TripId = trip.TripId,
                OperationKey = saga.VoidOperationKey,
                AmountMinor = amountMinor,
                Currency = trip.Fare.Currency,
            },
            messageId: VoidPaymentAuthorizationMessageId(trip.TripId),
            occurredAt: occurredAt,
            aggregateType: TripOutboxIntents.TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            sagaId: saga.SagaId.ToString("D"),
            operationKey: saga.VoidOperationKey);
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            VoidPaymentAuthorizationRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// MarkDriverTripStartedV1 for the Trip-side start step: the Driver service
    /// owns the authoritative RESERVED→ON_TRIP transition; this durable
    /// command carries the exact fenced identity Trip started under.
    /// </summary>
    public static OutboxMessage MarkDriverTripStarted(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.InProgress || trip.Assignment is null)
        {
            throw new InvalidOperationException(
                $"MarkDriverTripStarted requires an IN_PROGRESS Trip with a current assignment but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }

        var assignment = trip.Assignment;
        var envelope = MessageEnvelope<MarkDriverTripStartedV1>.Create(
            MarkDriverTripStartedType,
            trip.TripId.ToString("D"),
            new MarkDriverTripStartedV1
            {
                TripId = trip.TripId,
                DriverId = assignment.DriverId,
                ReservationId = assignment.ReservationId,
                MatchingRound = assignment.MatchingRound,
            },
            messageId: MarkDriverTripStartedMessageId(trip.TripId, assignment.ReservationId),
            occurredAt: occurredAt,
            aggregateType: TripOutboxIntents.TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            operationKey: MarkDriverTripStartedMessageId(trip.TripId, assignment.ReservationId));
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            MarkDriverTripStartedRoutingKey, now: occurredAt);
    }

    /// <summary>
    /// MarkDriverTripCompletedV1 for the Trip-side completion step: the Driver
    /// service owns the authoritative ON_TRIP→AVAILABLE transition.
    /// </summary>
    public static OutboxMessage MarkDriverTripCompleted(Trip trip, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(trip);
        if (trip.State != TripState.Completed || trip.Assignment is null)
        {
            throw new InvalidOperationException(
                $"MarkDriverTripCompleted requires a COMPLETED Trip with a current assignment but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }

        var assignment = trip.Assignment;
        var messageId = MarkDriverTripCompletedMessageId(trip.TripId, assignment.ReservationId);
        var envelope = MessageEnvelope<MarkDriverTripCompletedV1>.Create(
            MarkDriverTripCompletedType,
            trip.TripId.ToString("D"),
            new MarkDriverTripCompletedV1
            {
                TripId = trip.TripId,
                DriverId = assignment.DriverId,
                ReservationId = assignment.ReservationId,
                MatchingRound = assignment.MatchingRound,
            },
            messageId: messageId,
            occurredAt: occurredAt,
            aggregateType: TripOutboxIntents.TripAggregateType,
            aggregateId: trip.TripId.ToString("D"),
            aggregateVersion: trip.Version,
            operationKey: messageId);
        return OutboxMessage.Create(envelope, RideTopology.CommandsExchange,
            MarkDriverTripCompletedRoutingKey, now: occurredAt);
    }

    private static void RequireAcceptedTrip(Trip trip, string operation)
    {
        if (trip.State != TripState.DriverAccepted)
        {
            throw new InvalidOperationException(
                $"{operation} authorizes a DRIVER_ACCEPTED Trip but Trip {trip.TripId:D} is {trip.State.ToCanonicalName()}.");
        }
    }

    private static void RequireSagaLineage(Trip trip, RideSaga saga, RideSagaStep expectedStep, string operation)
    {
        if (saga.TripId != trip.TripId)
        {
            throw new InvalidOperationException($"{operation} requires the saga of the same Trip.");
        }

        if (saga.CurrentStep != expectedStep)
        {
            throw new InvalidOperationException(
                $"{operation} requires a {expectedStep.ToCanonicalName()} saga but the saga is {saga.CurrentStep.ToCanonicalName()}.");
        }
    }
}
