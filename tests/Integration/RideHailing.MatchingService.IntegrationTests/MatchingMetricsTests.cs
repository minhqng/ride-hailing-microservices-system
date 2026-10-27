// B-W12-02: MeterListener proof over the REAL Matching attempt ledger,
// terminalizer and orchestrator. Transport uncertainty is injected at the
// IDriverClient seam; every durable row, terminal result and Outbox intent
// remains production code.
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class MatchingMetricsTests
{
    private readonly MatchingPostgresDatabase _matching;

    public MatchingMetricsTests(MatchingPostgresDatabase matching)
    {
        _matching = matching;
    }

    private MatchingAttemptRepository Attempts(MatchingMetrics metrics) =>
        new(_matching.ConnectionString, metrics);

    private MatchingTerminalizer Terminalizer(MatchingMetrics metrics) =>
        new(_matching.DataSource, _matching.OutboxStore, metrics: metrics);

    private MatchingReserveOrchestrator Orchestrator(
        StubDriverClient stub, MatchingMetrics metrics) =>
        new(Attempts(metrics), stub, Terminalizer(metrics), metrics: metrics);

    [Fact]
    public async Task Success_Counts_Durable_Attempt_And_Round_Without_Unknown_Or_Terminal()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var stub = new StubDriverClient();
        var trip = Guid.NewGuid();
        var candidates = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var result = await Orchestrator(stub, metrics).ExecuteRoundAsync(
            trip, 1, candidates, Guid.NewGuid().ToString("D"));

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        Assert.Equal(1, capture.Counter("matching.attempt.created_total"));
        Assert.Equal(0, capture.Counter("matching.unknown_total"));
        Assert.Equal(0, capture.Counter("matching.no_driver_total"));
        Assert.Equal(0, capture.Counter("matching.reconcile_total"));
        Assert.Equal(1, capture.Counter("matching.round.total", "outcome=Reserved"));
        Assert.Equal(new long[] { 2 }, capture.LongHistogram("matching.round.candidate_count"));
        Assert.Single(capture.DoubleHistogram("matching.round.duration"));
    }

    [Fact]
    public async Task Exhaustion_Counts_One_Terminal_Decision_And_Terminal_Replay_Mints_None()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => Task.FromResult(
                new DriverReserveResult(
                    DriverReserveOutcomeKind.Rejected, "DRIVER_NOT_AVAILABLE", null)),
        };
        var trip = Guid.NewGuid();
        var candidates = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var input = Guid.NewGuid().ToString("D");

        var first = await Orchestrator(stub, metrics).ExecuteRoundAsync(
            trip, 1, candidates, input);
        Assert.Equal(RoundOutcome.NoDriver, first.Outcome);
        Assert.Equal(2, capture.Counter("matching.attempt.created_total"));
        Assert.Equal(0, capture.Counter("matching.unknown_total"));
        Assert.Equal(1, capture.Counter("matching.no_driver_total"));

        // Exact replay of an already terminal round: same durable result,
        // no new attempt, no second terminal business metric.
        var replay = await Orchestrator(stub, metrics).ExecuteRoundAsync(
            trip, 1, candidates, input);
        Assert.Equal(RoundOutcome.NoDriver, replay.Outcome);
        Assert.True(replay.Replay);
        Assert.Equal(2, capture.Counter("matching.attempt.created_total"));
        Assert.Equal(1, capture.Counter("matching.no_driver_total"));
        Assert.Equal(1, capture.Counter("matching.round.total", "replay=True"));

        // Terminal replay does not offer a live candidate sequence again.
        Assert.Single(capture.LongHistogram("matching.round.candidate_count"));
    }

    [Fact]
    public async Task Transport_Loss_Counts_Unknown_Once_And_Reconcile_Counts_Every_Poll()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var stub = new StubDriverClient
        {
            OnReserve = (_, _) => throw new HttpRequestException("response lost"),
            OnQuery = (_, _, _) => throw new HttpRequestException("driver unavailable"),
        };
        var trip = Guid.NewGuid();
        var candidates = new[] { Guid.NewGuid() };

        var round = await Orchestrator(stub, metrics).ExecuteRoundAsync(
            trip, 1, candidates, Guid.NewGuid().ToString("D"));
        Assert.Equal(RoundOutcome.BlockedOnUnknown, round.Outcome);
        Assert.Equal(1, capture.Counter("matching.attempt.created_total"));
        Assert.Equal(1, capture.Counter("matching.unknown_total", "reason=reserve_transport"));
        Assert.Equal(0, capture.Counter("matching.no_driver_total"));

        // Repeated uncertainty is three real polls, not a fabricated success.
        var attempts = Attempts(metrics);
        var unknown = Assert.Single(await attempts.LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, unknown.State);

        // A second reserve execution on the already-UNKNOWN attempt is a
        // replay of known uncertainty, not a second UNKNOWN transition.
        var repeated = await Orchestrator(stub, metrics).ExecuteAttemptAsync(
            trip, 1, unknown.Ordinal, candidates[0]);
        Assert.Equal(AttemptOutcome.Unknown, repeated.Outcome);
        Assert.Equal(1, capture.Counter("matching.unknown_total"));
        Assert.Equal(1, capture.Counter("matching.attempt.created_total"));

        var exhausted = await Orchestrator(stub, metrics).ReconcileAsync(
            trip, 1, unknown.Ordinal);
        Assert.Equal(AttemptOutcome.NeedsReview, exhausted.Outcome);
        Assert.Equal(3, capture.Counter("matching.reconcile_total", "outcome=transport_failure"));
        Assert.Equal(1, capture.Counter("matching.unknown_total"));

        // Same reservation identity resolves durably; no second UNKNOWN.
        stub.OnQuery = (_, _, _) => Task.FromResult(new ReservationReconcileResult(
            ReservationReconcileOutcome.OwnedByThisReservation, 4));
        var resolved = await Orchestrator(stub, metrics).ReconcileAsync(
            trip, 1, unknown.Ordinal);
        Assert.Equal(AttemptOutcome.Reserved, resolved.Outcome);
        Assert.Equal(unknown.ReservationId, resolved.ReservationId);
        Assert.Equal(1, capture.Counter(
            "matching.reconcile_total", "outcome=OWNED_BY_THIS_RESERVATION"));
        Assert.Equal(1, capture.Counter("matching.unknown_total"));
        var finalRound = await attempts.LoadRoundAsync(trip, 1);
        Assert.Equal(MatchingRoundStatuses.Reserved, finalRound!.Status);
        Assert.Equal(unknown.ReservationId, finalRound.SelectedReservationId);
    }

    [Fact]
    public async Task Throwing_Round_Is_Visible_In_Total_And_Duration()
    {
        using var capture = new MeterCapture();
        var metrics = new MatchingMetrics();
        var stub = new StubDriverClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Orchestrator(stub, metrics).ExecuteRoundAsync(
                Guid.NewGuid(), 0, [Guid.NewGuid()], "w12-invalid-round"));

        Assert.Equal(1, capture.Counter("matching.round.total", "outcome=error"));
        Assert.Single(capture.DoubleHistogram("matching.round.duration"));
    }

    private sealed class StubDriverClient : IDriverClient
    {
        public Func<DriverReserveRequest, CancellationToken, Task<DriverReserveResult>>? OnReserve;
        public Func<Guid, Guid, CancellationToken, Task<ReservationReconcileResult>>? OnQuery;

        public Task<DriverVersionRead?> TryGetDriverVersionAsync(
            Guid driverId, CancellationToken ct = default) =>
            Task.FromResult<DriverVersionRead?>(new DriverVersionRead(3, "AVAILABLE"));

        public Task<DriverReserveResult> ReserveAsync(
            DriverReserveRequest request, CancellationToken ct = default) =>
            OnReserve is not null
                ? OnReserve(request, ct)
                : Task.FromResult(new DriverReserveResult(
                    DriverReserveOutcomeKind.Reserved, null, 4));

        public Task<ReservationReconcileResult> GetReservationAsync(
            Guid driverId, Guid reservationId, CancellationToken ct = default) =>
            OnQuery is not null
                ? OnQuery(driverId, reservationId, ct)
                : Task.FromResult(new ReservationReconcileResult(
                    ReservationReconcileOutcome.NotCommittedOrAvailable, 3));
    }

    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<(string Name, string Tags), long> _counters = new();
        private readonly ConcurrentQueue<(string Name, double Value)> _doubleHistograms = new();
        private readonly ConcurrentQueue<(string Name, long Value)> _longHistograms = new();

        public MeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MatchingMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                _counters.AddOrUpdate(
                    (instrument.Name, TagString(tags)), value, (_, current) => current + value);
                if (instrument.Name == "matching.round.candidate_count")
                {
                    _longHistograms.Enqueue((instrument.Name, value));
                }
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
                _doubleHistograms.Enqueue((instrument.Name, value)));
            _listener.Start();
        }

        public long Counter(string name, string? tag = null) =>
            _counters
                .Where(pair => pair.Key.Name == name
                    && (tag is null || pair.Key.Tags.Contains(tag, StringComparison.Ordinal)))
                .Sum(pair => pair.Value);

        public IReadOnlyList<long> LongHistogram(string name) =>
            _longHistograms.Where(x => x.Name == name).Select(x => x.Value).ToArray();

        public IReadOnlyList<double> DoubleHistogram(string name) =>
            _doubleHistograms.Where(x => x.Name == name).Select(x => x.Value).ToArray();

        public void Dispose() => _listener.Dispose();

        private static string TagString(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var values = new List<string>(tags.Length);
            foreach (var tag in tags)
            {
                values.Add($"{tag.Key}={tag.Value}");
            }

            return string.Join(",", values);
        }
    }
}
