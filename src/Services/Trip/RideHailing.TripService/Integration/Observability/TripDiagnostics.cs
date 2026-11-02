// Trip-owned diagnostics instruments (A-W12-01/02, Doc 05A §20.2).
//
// OBSERVATION ONLY. Every instrument here is best-effort: recording never
// throws into a business path, never writes to the database and never
// influences a Trip/Saga/Outbox transaction. Logs and traces are diagnostic
// correlation, never authoritative recovery state (Doc 05A §20 mistake 7).
//
// NAMING. Instruments follow the repository dot convention (like C's
// outbox.pending); an OTel Prometheus exporter renders them as the canonical
// Doc 05A §20.2 names:
//
//   trip.created                 -> trip_created_total
//   trip.cancelled               -> trip_cancelled_total
//   trip.transition.rejected     -> trip_transition_rejected_total
//   saga.compensation            -> saga_compensation_total
//   saga.requires_review         -> saga_requires_review_total
//   saga.active{step}            -> saga_active{step}
//   (outbox.pending / outbox.oldest_pending.age stay C-owned shared
//    instruments; Trip reuses them and adds no duplicate measurement)
//
// CARDINALITY. Only bounded vocabulary values become dimensions: the
// `reason` tag carries domain rejection class names (never TripErrorCode
// messages), the `step` tag carries canonical RideSagaStep names. tripId,
// reservationId, sagaId, operationKey, messageId, riderId and traceId are
// span/log diagnostic fields (Doc 05A §20.2 safe-field list), never metric
// labels.
//
// SEMANTICS (frozen; see also the W12 telemetry tests):
//   - trip.created counts one COMMITTED new Trip, never a replay and never a
//     create-race reconciliation (the loser observed the winner's commit).
//   - trip.cancelled counts one COMMITTED new CANCELLED transition, never an
//     idempotent terminal replay.
//   - trip.transition.rejected counts DOMAIN rejections only (state-policy,
//     ownership/identity conflicts). Infrastructure failures (OCC conflicts,
//     transient database, broker) and pure input-validation 400s are not
//     business-transition rejections and are not counted.
//   - saga.compensation / saga.requires_review count COMMITTED NEW saga
//     status ENTRIES into COMPENSATING / REQUIRES_REVIEW. Redelivered
//     messages never reach these increments: the durable receipt replay path
//     returns before the domain applies anything.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RideHailing.TripService.Integration.Observability;

/// <summary>Trip domain ActivitySource and low-cardinality metric instruments.</summary>
public static class TripDiagnostics
{
    public const string ActivitySourceName = "RideHailing.Trip";
    public const string MeterName = "RideHailing.Trip";

    public static readonly ActivitySource Activities = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> TripCreated =
        Meter.CreateCounter<long>("trip.created", "trips", "Committed new Trip creations.");
    private static readonly Counter<long> TripCancelled =
        Meter.CreateCounter<long>("trip.cancelled", "trips", "Committed new CANCELLED transitions.");
    private static readonly Counter<long> TransitionRejected =
        Meter.CreateCounter<long>("trip.transition.rejected", "transitions",
            "Domain-rejected Trip/authority transitions.");
    private static readonly Counter<long> SagaCompensation =
        Meter.CreateCounter<long>("saga.compensation", "sagas",
            "Committed new saga entries into COMPENSATING.");
    private static readonly Counter<long> SagaRequiresReview =
        Meter.CreateCounter<long>("saga.requires_review", "sagas",
            "Committed new saga entries into REQUIRES_REVIEW.");

    // Durable-state snapshot refreshed by TripMetricsObserver; an ObservableGauge
    // re-reads this on scrape, so after a process restart the gauge re-derives
    // from the database instead of a lost in-memory count.
    private static volatile TripSagaBacklogSnapshot sagaBacklog =
        new(new Dictionary<string, long>(), null);

    private static readonly ObservableGauge<long> SagaActive = Meter.CreateObservableGauge<long>(
        "saga.active",
        () => SnapshotSagaActive(),
        "sagas",
        "Non-terminal sagas (ACTIVE, COMPENSATING, REQUIRES_REVIEW) per current step, from durable rows.");

    private static readonly ObservableGauge<double> SagaOldestPendingAge = Meter.CreateObservableGauge<double>(
        "saga.oldest_pending.age", () => SnapshotSagaAge(), "s",
        "Age since creation of the oldest non-terminal durable saga; absent before a successful database observation.");

    /// <summary>Bounded domain-rejection reasons used as the only metric dimension.</summary>
    public static class RejectionReasons
    {
        public const string ActiveTripConflict = "active_trip_conflict";
        public const string IdempotencyConflict = "idempotency_conflict";
        public const string InvalidTransition = "invalid_transition";
        public const string TerminalState = "terminal_state";
        public const string CancellationPolicyUnresolved = "cancellation_policy_unresolved";
        public const string StaleIdentity = "stale_identity";
        public const string StatusConflict = "status_conflict";
        public const string IdentityRequired = "identity_required";
        public const string AuthorityRejected = "authority_rejected";
    }

    // Static constructors do not run until the type is touched; touch the
    // gauge so it exists even if no counter fires first.
    static TripDiagnostics() { _ = SagaActive; _ = SagaOldestPendingAge; }

    public static void RecordTripCreated() => TripCreated.Add(1);

    public static void RecordTripCancelled() => TripCancelled.Add(1);

    public static void RecordTransitionRejected(string reason) =>
        TransitionRejected.Add(1, new TagList { { "reason", reason } });

    public static void RecordSagaCompensation() => SagaCompensation.Add(1);

    public static void RecordSagaRequiresReview() => SagaRequiresReview.Add(1);

    /// <summary>
    /// Replaces the durable-state snapshot behind saga.active. Called only by
    /// <see cref="TripMetricsObserver"/> after a successful bounded database
    /// query; an observation fault keeps the previous snapshot rather than
    /// inventing a zero.
    /// </summary>
    public static void ObserveSagaActive(IReadOnlyDictionary<string, long> countsByStep) =>
        sagaBacklog = new(new Dictionary<string, long>(countsByStep, StringComparer.Ordinal), null);

    public static void ObserveSagaBacklog(TripSagaBacklogSnapshot snapshot) =>
        sagaBacklog = new(new Dictionary<string, long>(snapshot.CountsByStep, StringComparer.Ordinal), snapshot.OldestAgeSeconds);

    private static IEnumerable<Measurement<long>> SnapshotSagaActive()
    {
        foreach (var (step, count) in sagaBacklog.CountsByStep)
            yield return new Measurement<long>(count, new TagList { { "step", step } });
    }

    private static IEnumerable<Measurement<double>> SnapshotSagaAge()
    {
        if (sagaBacklog.OldestAgeSeconds is { } seconds)
            yield return new Measurement<double>(seconds);
    }
}
