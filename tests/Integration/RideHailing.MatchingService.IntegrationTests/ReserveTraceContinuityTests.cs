// Span-chain proof across a REAL Kestrel/HTTP boundary and the
// REAL Driver transaction. The ActivityListener is the only test double;
// every span below is produced by production code.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Application.Location;
using RideHailing.DriverService.Application.Messaging;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Faults;
using RideHailing.DriverService.Infrastructure.Persistence;
using RideHailing.DriverService.Infrastructure.Telemetry;
using RideHailing.MatchingService.Application.Reserve;
using RideHailing.MatchingService.Infrastructure.DriverClient;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Outbox;
using RideHailing.Observability;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class ReserveTraceContinuityTests
{
    private const string FaultName = DriverFaultPointNames.AfterReserveCommitBeforeResponse;
    private static readonly ActivitySource WorkflowSource = new("Reserve.Test.Workflow");

    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;
    private readonly ITestOutputHelper _output;

    public ReserveTraceContinuityTests(
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers,
        ITestOutputHelper output)
    {
        _matching = matching;
        _drivers = drivers;
        _output = output;
    }

    [Fact]
    public async Task SuccessfulReserve_Produces_One_Parent_Chain_To_The_Driver_Transaction()
    {
        using var capture = new ActivityCapture();
        var driver = await SeedAvailableDriverAsync();
        await using var host = BuildDriverHost(DriverFaultPoint.Disabled());
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address + "/") };
        var orchestrator = Orchestrator(http);
        var trip = Guid.NewGuid();

        using var root = WorkflowSource.StartActivity("workflow")
            ?? throw new InvalidOperationException("Workflow activity was not sampled.");
        var result = await orchestrator.ExecuteRoundAsync(
            trip, 1, [driver.DriverId], $"trace:{trip:N}");

        // A location request exercises the privacy assertion on a payload that
        // actually carries raw coordinates and a device session.
        using var location = await http.PostAsJsonAsync(
            $"drivers/{driver.DriverId:N}/location",
            new
            {
                deviceSessionId = "trace-location",
                sequenceNo = 1,
                capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                latitude = 10.8,
                longitude = 106.7,
            });
        Assert.Equal(HttpStatusCode.Accepted, location.StatusCode);
        root.Stop();

        Assert.Equal(RoundOutcome.Reserved, result.Outcome);
        var traceId = root.TraceId;
        var workflow = SingleSpan(capture, traceId, "workflow");
        var round = SingleSpan(capture, traceId, "matching.round.execute");
        Assert.Equal(workflow.SpanId, round.ParentSpanId);
        var attempt = SingleSpan(capture, traceId, "matching.attempt.reserve");
        var transaction = SingleSpan(capture, traceId, "driver.reserve");
        var httpSpan = Assert.Single(capture.Spans, span =>
            span.TraceId == traceId
            && span.Source == "System.Net.Http"
            && span.ParentSpanId == attempt.SpanId
            && span.Name == "POST");
        var server = Assert.Single(capture.Spans, span =>
            span.TraceId == traceId
            && span.Source.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            && span.ParentSpanId == httpSpan.SpanId);

        Assert.Equal(round.SpanId, attempt.ParentSpanId);
        Assert.Equal(attempt.SpanId, httpSpan.ParentSpanId);
        Assert.Equal(httpSpan.SpanId, server.ParentSpanId);
        Assert.Equal(server.SpanId, transaction.ParentSpanId);
        Assert.Equal("Reserved", round.Tags["matching.round.outcome"]);
        Assert.Equal("Reserved", attempt.Tags["matching.attempt.outcome"]);
        Assert.Equal("won", transaction.Tags["driver.reserve.outcome"]);
        Assert.Equal(result.ReservationId!.Value.ToString("D"), transaction.Tags["reservation.id"]);

        AssertNoSensitiveTelemetry(capture, traceId);
        _output.WriteLine(
            $"trace={traceId} round={round.SpanId} attempt={attempt.SpanId} " +
            $"http={httpSpan.SpanId} server={server.SpanId} driverTxn={transaction.SpanId}");

        await host.StopAsync();
    }

    [Fact]
    public async Task LostResponse_Classifies_Unknown_Without_Fabricating_A_Success_Then_Resolves_SameReservation()
    {
        using var capture = new ActivityCapture();
        var driver = await SeedAvailableDriverAsync();
        var faults = DriverFaultPoint.ForTesting([FaultName], TimeSpan.FromSeconds(30));
        await using var host = BuildDriverHost(faults);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var reserveHttp = new HttpClient
        {
            BaseAddress = new Uri(address + "/"),
            Timeout = TimeSpan.FromSeconds(2),
        };
        using var reconcileHttp = new HttpClient
        {
            BaseAddress = new Uri(address + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        var orchestrator = Orchestrator(reserveHttp);
        var reconciler = Orchestrator(reconcileHttp);
        var trip = Guid.NewGuid();

        using var root = WorkflowSource.StartActivity("workflow")
            ?? throw new InvalidOperationException("Workflow activity was not sampled.");
        var roundTask = orchestrator.ExecuteRoundAsync(
            trip, 1, [driver.DriverId], $"trace-rel01:{trip:N}");
        await faults.WaitForReachedAsync(TimeSpan.FromSeconds(10));
        var result = await roundTask.WaitAsync(TimeSpan.FromSeconds(15));
        root.Stop();

        Assert.Equal(RoundOutcome.BlockedOnUnknown, result.Outcome);
        var traceId = root.TraceId;
        var round = SingleSpan(capture, traceId, "matching.round.execute");
        var attempt = SingleSpan(capture, traceId, "matching.attempt.reserve");
        var transaction = SingleSpan(capture, traceId, "driver.reserve");
        var httpSpan = Assert.Single(capture.Spans, span =>
            span.TraceId == traceId
            && span.Source == "System.Net.Http"
            && span.ParentSpanId == attempt.SpanId
            && span.Name == "POST");

        Assert.Equal("BlockedOnUnknown", round.Tags["matching.round.outcome"]);
        Assert.Equal("Unknown", attempt.Tags["matching.attempt.outcome"]);
        Assert.Equal("RESERVE_TRANSPORT", attempt.Tags["matching.unknown.reason"]);
        Assert.Equal("won", transaction.Tags["driver.reserve.outcome"]);
        // The caller never received a response, so no successful response span
        // may exist for this call.
        Assert.False(httpSpan.Tags.TryGetValue("http.response.status_code", out var status)
            && status == "200");
        Assert.Equal(ActivityStatusCode.Error, httpSpan.Status);

        Assert.True(faults.Release(FaultName));
        var persisted = Assert.Single(await Attempts().LoadAttemptsAsync(trip, 1));
        Assert.Equal(MatchingAttemptStates.Unknown, persisted.State);
        var reservationId = persisted.ReservationId;

        // Resume the round trace through a REAL captured workflow span. The
        // ledger does not persist traceparent, so production callers must
        // supply a workflow/consumer span (documented limitation).
        using var reconcileRoot = WorkflowSource.StartActivity(
            "workflow.reconcile",
            ActivityKind.Internal,
            new ActivityContext(traceId, round.SpanId, ActivityTraceFlags.Recorded))
            ?? throw new InvalidOperationException("Reconcile workflow activity was not sampled.");
        var resolved = await reconciler.ReconcileAsync(trip, 1, persisted.Ordinal);
        reconcileRoot.Stop();

        Assert.Equal(AttemptOutcome.Reserved, resolved.Outcome);
        Assert.Equal(reservationId, resolved.ReservationId);
        var reconcileWorkflow = SingleSpan(capture, traceId, "workflow.reconcile");
        Assert.Equal(round.SpanId, reconcileWorkflow.ParentSpanId);
        var reconcileSpan = SingleSpan(capture, traceId, "matching.attempt.reconcile");
        Assert.Equal(reconcileRoot.SpanId, reconcileSpan.ParentSpanId);
        Assert.Equal("Reserved", reconcileSpan.Tags["matching.reconcile.outcome"]);
        Assert.Equal(reservationId.ToString("D"), reconcileSpan.Tags["reservation.id"]);
        var reconcileHttpSpan = capture.Spans.Last(span =>
            span.TraceId == traceId
            && span.Source == "System.Net.Http"
            && span.ParentSpanId == reconcileSpan.SpanId);
        var reconcileServer = Assert.Single(capture.Spans, span =>
            span.TraceId == traceId
            && span.Source.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            && span.ParentSpanId == reconcileHttpSpan.SpanId);
        Assert.NotEqual(reconcileSpan.SpanId, reconcileServer.SpanId);

        AssertNoSensitiveTelemetry(capture, traceId);
        _output.WriteLine(
            $"REL01 trace={traceId} round={round.SpanId} attempt={attempt.SpanId} " +
            $"http={httpSpan.SpanId} driverTxn={transaction.SpanId} " +
            $"reconcile={reconcileSpan.SpanId} reservation={reservationId:D} " +
            $"attemptOutcome=Unknown driverTxnOutcome=won finalRound=RESERVED");

        await host.StopAsync();
    }

    [Fact]
    public async Task Rejected_Reserve_And_Stale_Release_Are_Classified_On_Their_Own_Spans()
    {
        using var capture = new ActivityCapture();
        var driver = await SeedAvailableDriverAsync();
        await using var host = BuildDriverHost(DriverFaultPoint.Disabled());
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address + "/") };
        var repository = _drivers.CreateRepository();

        using var root = WorkflowSource.StartActivity("workflow")
            ?? throw new InvalidOperationException("Workflow activity was not sampled.");
        var reserve = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/reserve", new
        {
            tripId = Guid.NewGuid(),
            reservationId = Guid.NewGuid(),
            expectedVersion = driver.Version,
            matchingRound = 1,
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, reserve.StatusCode);
        var current = (await repository.LoadAsync(driver.DriverId))!;
        var tripId = current.ReservedTripId!.Value;

        var rejected = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/reserve", new
        {
            tripId = Guid.NewGuid(),
            reservationId = Guid.NewGuid(),
            expectedVersion = current.Version,
            matchingRound = 1,
        });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);

        var staleRelease = await http.PostAsJsonAsync($"drivers/{driver.DriverId:N}/release", new
        {
            tripId,
            reservationId = Guid.NewGuid(),
        });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, staleRelease.StatusCode);
        root.Stop();

        var txnSpans = capture.Spans
            .Where(span => span.TraceId == root.TraceId && span.Name == "driver.reserve")
            .ToList();
        Assert.Equal(2, txnSpans.Count);
        Assert.Equal(new[] { "won", "conflict" }, txnSpans.Select(s => s.Tags["driver.reserve.outcome"]));
        var releaseSpan = Assert.Single(capture.Spans, span =>
            span.TraceId == root.TraceId && span.Name == "driver.release");
        Assert.Equal("conflict", releaseSpan.Tags["driver.release.outcome"]);
        AssertNoSensitiveTelemetry(capture, root.TraceId);
        _output.WriteLine(
            $"REJECT trace={root.TraceId} reserveOutcomes={string.Join(",", txnSpans.Select(s => s.Tags["driver.reserve.outcome"]))} " +
            $"releaseOutcome=conflict httpStatuses=200,409,409");

        await host.StopAsync();
    }

    private MatchingAttemptRepository Attempts() => new(_matching.ConnectionString);

    private MatchingReserveOrchestrator Orchestrator(HttpClient http) =>
        new(Attempts(), new HttpDriverClient(http),
            new MatchingTerminalizer(_matching.DataSource, _matching.OutboxStore));

    private async Task<Driver> SeedAvailableDriverAsync()
    {
        var repo = _drivers.CreateRepository();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver));
        return driver;
    }

    private WebApplication BuildDriverHost(DriverFaultPoint faults)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddRideTelemetry("driver-service-test");
        builder.AddDriverTelemetry();

        var outbox = new NpgsqlOutboxStore(_drivers.DataSource);
        builder.Services.AddSingleton(_drivers.DataSource);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton<IOutboxStore>(outbox);
        builder.Services.AddSingleton(new DriverRepository(_drivers.DataSource, outbox));
        builder.Services.AddSingleton(faults);
        builder.Services.AddOptions<LocationIngressOptions>();
        builder.Services.AddOptions<DriverBrokerOptions>()
            .Configure(options => options.PublishingEnabled = false);
        builder.Services.AddSingleton<LocationAcceptor>();
        builder.Services.AddSingleton<DriverEventPublisher>();

        var app = builder.Build();
        DriverEndpoints.Map(app);
        return app;
    }

    private static ActivitySnapshot SingleSpan(
        ActivityCapture capture, ActivityTraceId traceId, string sourceOrName)
    {
        var matches = capture.Spans
            .Where(span => span.TraceId == traceId
                && (span.Source == sourceOrName || span.Name == sourceOrName))
            .ToList();
        Assert.NotEmpty(matches);
        return Assert.Single(matches);
    }

    private static void AssertNoSensitiveTelemetry(ActivityCapture capture, ActivityTraceId traceId)
    {
        foreach (var span in capture.Spans.Where(s => s.TraceId == traceId))
        {
            foreach (var tag in span.Tags)
            {
                Assert.DoesNotContain("authorization", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("token", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sessionId", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("latitude", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("longitude", tag.Key, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Bearer", tag.Value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("106.6", tag.Value, StringComparison.Ordinal);
                Assert.DoesNotContain("106.7", tag.Value, StringComparison.Ordinal);
                Assert.DoesNotContain("trace-location", tag.Value, StringComparison.Ordinal);
            }
        }
    }

    private sealed record ActivitySnapshot(
        string Source,
        string Name,
        ActivityTraceId TraceId,
        ActivitySpanId SpanId,
        ActivitySpanId ParentSpanId,
        ActivityStatusCode Status,
        IReadOnlyDictionary<string, string> Tags);

    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<ActivitySnapshot> _spans = new();

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source =>
                    source.Name is "RideHailing.MatchingService"
                        or "RideHailing.DriverService"
                        or "System.Net.Http"
                        or "Reserve.Test.Workflow"
                    || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllData,
                ActivityStopped = activity => _spans.Enqueue(new ActivitySnapshot(
                    activity.Source.Name,
                    activity.DisplayName,
                    activity.TraceId,
                    activity.SpanId,
                    activity.ParentSpanId,
                    activity.Status,
                    activity.TagObjects.ToDictionary(
                        tag => tag.Key, tag => tag.Value?.ToString() ?? ""))),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<ActivitySnapshot> Spans => _spans.ToArray();

        public void Dispose() => _listener.Dispose();
    }
}
