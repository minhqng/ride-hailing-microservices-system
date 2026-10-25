// Trip saga payment/driver intent identity (A-W10-02/04/05/06/07).
//
// ORACLE: every saga obligation derives its messageId from the exact
// business identity and its operationKey from the frozen per-trip lineage
// (Doc 05A §18.4). A retry, redelivery or restart rebuilds the IDENTICAL
// logical message — never a fresh identity — and the payload quotes the
// immutable frozen fare (DSI-014) deterministically converted to minor
// units. Capture intents are constructible ONLY from a COMPLETED Trip
// (DSI-027) and void intents only from a CANCELLED one.

using System.Text.Json;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Integration.Messaging;
using RideHailing.TripService.Integration.Payment;

namespace RideHailing.TripService.UnitTests;

public sealed class TripSagaIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid DriverId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static Trip TripAt(Func<Trip, bool> advance)
    {
        var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6),
            new(10.8, 106.7), new("VND", 50000m, "w10", Now), Now);
        Assert.True(advance(trip));
        return trip;
    }

    private static Trip Accepted() => TripAt(t =>
        t.StartMatching(Now).IsSuccess && t.ApplyDriverReserved(DriverId, ReservationId, 1, Now).IsSuccess &&
        t.RecordDriverAccepted(DriverId, ReservationId, Now).IsSuccess);

    private static Trip ReadyToStart() => TripAt(t =>
        t.StartMatching(Now).IsSuccess && t.ApplyDriverReserved(DriverId, ReservationId, 1, Now).IsSuccess &&
        t.RecordDriverAccepted(DriverId, ReservationId, Now).IsSuccess &&
        t.MarkPaymentAuthorized(Now).IsSuccess);

    private static Trip InProgressRide() => TripAt(t =>
        t.StartMatching(Now).IsSuccess && t.ApplyDriverReserved(DriverId, ReservationId, 1, Now).IsSuccess &&
        t.RecordDriverAccepted(DriverId, ReservationId, Now).IsSuccess &&
        t.MarkPaymentAuthorized(Now).IsSuccess && t.Start(DriverId, ReservationId, Now).IsSuccess);

    private static Trip CompletedRide() => TripAt(t =>
        t.StartMatching(Now).IsSuccess && t.ApplyDriverReserved(DriverId, ReservationId, 1, Now).IsSuccess &&
        t.RecordDriverAccepted(DriverId, ReservationId, Now).IsSuccess &&
        t.MarkPaymentAuthorized(Now).IsSuccess && t.Start(DriverId, ReservationId, Now).IsSuccess &&
        t.Complete(DriverId, ReservationId, Now).IsSuccess);

    private static Trip CancelledAtAccepted() => TripAt(t =>
        t.StartMatching(Now).IsSuccess && t.ApplyDriverReserved(DriverId, ReservationId, 1, Now).IsSuccess &&
        t.RecordDriverAccepted(DriverId, ReservationId, Now).IsSuccess && t.Cancel(Now).IsSuccess);

    private static RideSaga SagaFor(Trip trip, RideSagaStep step)
    {
        var saga = RideSaga.Start(Guid.NewGuid(), trip.TripId, ReservationId,
            RideSagaOperationKeys.Authorize(trip.TripId), Now);
        if (step == RideSagaStep.AuthorizePending) return saga;
        Assert.True(saga.RecordAuthorizeSucceeded(Now).IsSuccess);
        if (step == RideSagaStep.Authorized) return saga;
        if (step == RideSagaStep.CapturePending)
        {
            Assert.True(saga.RecordRideCompleted(RideSagaOperationKeys.Capture(trip.TripId), Now).IsSuccess);
            return saga;
        }

        throw new NotSupportedException($"Unexpected step {step}");
    }

    // ------------------------------------------------------------ authorize

    [Fact]
    public void AuthorizePayment_CarriesFrozenLineage_StableAcrossRebuilds()
    {
        var trip = Accepted();
        var saga = SagaFor(trip, RideSagaStep.AuthorizePending);

        var first = TripSagaIntents.AuthorizePayment(trip, saga, Now);
        var rebuilt = TripSagaIntents.AuthorizePayment(trip, saga, Now.AddMinutes(5));

        Assert.Equal($"trip:{trip.TripId:N}:authorize:v1", first.MessageId);
        Assert.Equal(first.MessageId, rebuilt.MessageId);
        Assert.Equal(first.PayloadJson, rebuilt.PayloadJson);
        Assert.Equal($"{trip.TripId:D}:authorize:v1", first.OperationKey);
        Assert.Equal(saga.SagaId.ToString("D"), first.SagaId);
        Assert.Equal("Trip", first.AggregateType);
        Assert.Equal(trip.TripId.ToString("D"), first.AggregateId);
        Assert.Equal(trip.Version, first.AggregateVersion);
        Assert.Equal(RideTopology.CommandsExchange, first.Exchange);
        Assert.Equal("payment.authorize.v1", first.RoutingKey);
        Assert.Equal(trip.TripId.ToString("D"), first.CorrelationId);

        var payload = JsonDocument.Parse(first.PayloadJson).RootElement;
        Assert.Equal(trip.TripId, payload.GetProperty("tripId").GetGuid());
        // VND has no minor unit: the frozen 50000 VND fare quotes 50000 minor.
        Assert.Equal(50000L, payload.GetProperty("amountMinor").GetInt64());
        Assert.Equal("VND", payload.GetProperty("currency").GetString());
    }

    [Fact]
    public void AuthorizePayment_RequiresTheAcceptedTripAndPendingSaga()
    {
        var accepted = Accepted();
        var advancedSaga = SagaFor(accepted, RideSagaStep.Authorized);
        Assert.Throws<InvalidOperationException>(() =>
            TripSagaIntents.AuthorizePayment(accepted, advancedSaga, Now));

        var matchingOnly = TripAt(t => t.StartMatching(Now).IsSuccess);
        var pendingSaga = SagaFor(matchingOnly, RideSagaStep.AuthorizePending);
        Assert.Throws<InvalidOperationException>(() =>
            TripSagaIntents.AuthorizePayment(matchingOnly, pendingSaga, Now));
    }

    // -------------------------------------------------------------- capture

    [Fact]
    public void CapturePayment_CarriesStableCaptureLineage_AndRequiresCompletedTrip()
    {
        var trip = CompletedRide();
        var saga = SagaFor(trip, RideSagaStep.CapturePending);

        var intent = TripSagaIntents.CapturePayment(trip, saga, Now);

        Assert.Equal($"trip:{trip.TripId:N}:capture:v1", intent.MessageId);
        Assert.Equal($"{trip.TripId:D}:capture:v1", intent.OperationKey);
        Assert.Equal(saga.SagaId.ToString("D"), intent.SagaId);
        Assert.Equal(RideTopology.CommandsExchange, intent.Exchange);
        Assert.Equal("payment.capture.v1", intent.RoutingKey);

        // DSI-027: capture is constructible only after the completion pivot.
        var notCompleted = InProgressRide();
        var authorizedSaga = SagaFor(notCompleted, RideSagaStep.Authorized);
        Assert.Throws<InvalidOperationException>(() =>
            TripSagaIntents.CapturePayment(notCompleted, authorizedSaga, Now));
    }

    // ---------------------------------------------------------------- void

    [Fact]
    public void VoidPaymentAuthorization_CompensatesOnlyACancelledTrip()
    {
        var trip = CancelledAtAccepted();
        var saga = SagaFor(trip, RideSagaStep.AuthorizePending);
        Assert.True(saga.RecordCancellationDuringAuthorize(Now).IsSuccess);
        Assert.True(saga.RecordAuthorizeSucceededAfterCancellation(
            RideSagaOperationKeys.Void(trip.TripId), Now).IsSuccess);

        var intent = TripSagaIntents.VoidPaymentAuthorization(trip, saga, Now);

        Assert.Equal($"trip:{trip.TripId:N}:void:v1", intent.MessageId);
        Assert.Equal($"{trip.TripId:D}:void:v1", intent.OperationKey);
        Assert.Equal(saga.SagaId.ToString("D"), intent.SagaId);
        Assert.Equal("payment.void-authorization.v1", intent.RoutingKey);

        // The three logical payment operations of one ride never collide.
        Assert.NotEqual(TripSagaIntents.AuthorizePaymentMessageId(trip.TripId), intent.MessageId);
        Assert.NotEqual(TripSagaIntents.CapturePaymentMessageId(trip.TripId), intent.MessageId);

        var accepted = Accepted();
        var pending = SagaFor(accepted, RideSagaStep.AuthorizePending);
        Assert.Throws<InvalidOperationException>(() =>
            TripSagaIntents.VoidPaymentAuthorization(accepted, pending, Now));
    }

    // ------------------------------------------------------ driver commands

    [Fact]
    public void MarkDriverCommands_CarryExactFencedIdentity_AndStableMessageIds()
    {
        var started = InProgressRide();
        var start = TripSagaIntents.MarkDriverTripStarted(started, Now);
        Assert.Equal($"trip:{started.TripId:N}:mark-started:{ReservationId:N}", start.MessageId);
        Assert.Equal("driver.mark-trip-started.v1", start.RoutingKey);
        Assert.Equal(RideTopology.CommandsExchange, start.Exchange);

        var completed = CompletedRide();
        var complete = TripSagaIntents.MarkDriverTripCompleted(completed, Now);
        Assert.Equal($"trip:{completed.TripId:N}:mark-completed:{ReservationId:N}", complete.MessageId);
        Assert.Equal("driver.mark-trip-completed.v1", complete.RoutingKey);

        var startPayload = JsonDocument.Parse(start.PayloadJson).RootElement;
        Assert.Equal(started.TripId, startPayload.GetProperty("tripId").GetGuid());
        Assert.Equal(DriverId, startPayload.GetProperty("driverId").GetGuid());
        Assert.Equal(ReservationId, startPayload.GetProperty("reservationId").GetGuid());
        Assert.Equal(1, startPayload.GetProperty("matchingRound").GetInt32());
        Assert.Equal("Trip", start.AggregateType);

        var completePayload = JsonDocument.Parse(complete.PayloadJson).RootElement;
        Assert.Equal(completed.TripId, completePayload.GetProperty("tripId").GetGuid());
        Assert.Equal(DriverId, completePayload.GetProperty("driverId").GetGuid());
        Assert.Equal(ReservationId, completePayload.GetProperty("reservationId").GetGuid());
        Assert.Equal(1, completePayload.GetProperty("matchingRound").GetInt32());

        // Wrong-state trips cannot mint driver lifecycle commands.
        Assert.Throws<InvalidOperationException>(() =>
            TripSagaIntents.MarkDriverTripStarted(ReadyToStart(), Now));
        Assert.Throws<InvalidOperationException>(() =>
            TripSagaIntents.MarkDriverTripCompleted(InProgressRide(), Now));
    }

    // --------------------------------------------------------- fare amounts

    [Fact]
    public void PaymentAmounts_ConvertTheFrozenFareDeterministically()
    {
        Assert.Equal(50000L, PaymentAmounts.ToMinorUnits(new FareSnapshot("VND", 50000m, "p", Now)));
        Assert.Equal(500000L, PaymentAmounts.ToMinorUnits(new FareSnapshot("USD", 5000m, "p", Now)));
        Assert.Equal(1250L, PaymentAmounts.ToMinorUnits(new FareSnapshot("EUR", 12.50m, "p", Now)));
        Assert.Equal(12L, PaymentAmounts.ToMinorUnits(new FareSnapshot("USD", 0.123m, "p", Now)));
        // A payment operation needs a positive amount; zero/invalid fares
        // must fail loudly at intent construction, never on the wire.
        Assert.Throws<InvalidOperationException>(() =>
            PaymentAmounts.ToMinorUnits(new FareSnapshot("VND", 0m, "p", Now)));
        Assert.Throws<InvalidOperationException>(() =>
            PaymentAmounts.ToMinorUnits(new FareSnapshot("USD", 0.001m, "p", Now)));
    }
}
