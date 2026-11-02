// A-W11 process-level fault harness.
//
// Launches the REAL compiled Trip service as an owned OS child process
// against disposable PostgreSQL (probe databases on the shared Testcontainers
// server) and a real RabbitMQ broker, drives committed business states
// through the production handler chain, publishes authority-shaped facts
// through the real broker topology, and observes ONLY authoritative state
// (database rows, queue contents, process exit). Every wait is a bounded
// poll on an authoritative oracle — no sleep ever stands in for a barrier
// (Doc 03 §1.5, Doc 05A §19.2 A-W11-06).
//
// The harness owns every process it terminates: it launches the apphost
// itself, kills the exact PID tree, and never matches or signals any other
// process. Infrastructure containers belong to the xUnit fixtures and are
// never stopped here.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Reliability;
using RideHailing.Messaging.Topology;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Sagas;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

/// <summary>Bounded poll on an authoritative predicate; failure names what was awaited.</summary>
internal static class TripW11Wait
{
    public static async Task UntilAsync(string what, Func<Task<bool>> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            if (await predicate())
            {
                return;
            }

            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0}s waiting for {what}.");
            }
        }
    }
}

/// <summary>
/// Scenario plumbing shared by the W11 fault proofs: probe databases, real
/// handler-chain seeding, envelope builders and broker queue access.
/// </summary>
internal static class TripW11Harness
{
    public static TripDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(connectionString).Options);

    public static async Task<string> NewProbeDatabaseAsync(string fixtureConnectionString, string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        var maintenance = new Npgsql.NpgsqlConnectionStringBuilder(fixtureConnectionString)
        {
            Database = "postgres",
        }.ToString();
        await using (var db = Context(maintenance))
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(fixtureConnectionString)
        {
            Database = name,
        }.ToString();
        await using (var db = Context(connectionString))
        {
            await db.Database.MigrateAsync();
        }

        return connectionString;
    }

    /// <summary>The committed state a producer leaves after the DriverAccepted
    /// transition: Trip DRIVER_ACCEPTED + saga AUTHORIZE_PENDING + the pending
    /// AuthorizePayment outbox intent, all staged through the REAL handler
    /// chain (the identical artifacts the service itself commits).</summary>
    public sealed record SeededSaga(
        Guid TripId, Guid RiderId, Guid DriverId, Guid ReservationId,
        Guid SagaId, string AuthorizeOperationKey, int SagaStepVersion, string AuthorizeMessageId);

    public static async Task<SeededSaga> SeedDriverAcceptedAuthorizePendingAsync(string connectionString)
    {
        var tripId = Guid.NewGuid();
        var riderId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var clock = TimeProvider.System;
        var pickup = new TripLocation(10.7, 106.6);
        var dropoff = new TripLocation(10.8, 106.7);

        await using (var db = Context(connectionString))
        {
            db.Trips.Add(Trip.Create(tripId, riderId, pickup, dropoff,
                new FareSnapshot("VND", 50000m, "w11", clock.GetUtcNow()), clock.GetUtcNow()));
            await db.SaveChangesAsync();
            Assert.True((await new StartMatchingHandler(db, clock, NullLogger<StartMatchingHandler>.Instance)
                .HandleAsync(tripId, riderId)).IsSuccess);
        }

        await using (var db = Context(connectionString))
        {
            Assert.True((await new RecordDriverReservationHandler(db, clock,
                NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId, 1)).IsSuccess);
        }

        await using (var db = Context(connectionString))
        {
            Assert.True((await new RecordDriverAcceptedHandler(db, clock,
                NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(tripId, driverId, reservationId)).IsSuccess);
        }

        return await ReadSeededSagaAsync(connectionString, tripId, riderId, driverId, reservationId);
    }

    /// <summary>
    /// The committed state a rider cancellation racing an in-flight
    /// authorization leaves: Trip CANCELLED + saga COMPENSATING/AUTHORIZE_PENDING
    /// with release/cancel-round/reconcile obligations outstanding.
    /// </summary>
    public static async Task<SeededSaga> SeedCancelledDuringAuthorizeAsync(string connectionString)
    {
        var seed = await SeedDriverAcceptedAuthorizePendingAsync(connectionString);
        await using (var db = Context(connectionString))
        {
            Assert.True((await new CancelRideHandler(db, TimeProvider.System,
                NullLogger<CancelRideHandler>.Instance).HandleAsync(seed.TripId, seed.RiderId)).IsSuccess);
        }

        return await ReadSeededSagaAsync(connectionString, seed.TripId, seed.RiderId, seed.DriverId,
            seed.ReservationId);
    }

    private static async Task<SeededSaga> ReadSeededSagaAsync(
        string connectionString, Guid tripId, Guid riderId, Guid driverId, Guid reservationId)
    {
        await using var db = Context(connectionString);
        var saga = await db.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId);
        var intent = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.MessageId == TripSagaIntents.AuthorizePaymentMessageId(tripId));
        Assert.Equal("PENDING", intent.Status);
        return new SeededSaga(tripId, riderId, driverId, reservationId, saga.SagaId,
            saga.AuthorizeOperationKey!, saga.StepVersion, intent.MessageId);
    }

    // ---------------------------------------------------------- authoritative reads

    public static async Task<Trip> TripAsync(string connectionString, Guid tripId)
    {
        await using var db = Context(connectionString);
        return await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
    }

    public static async Task<RideSaga> SagaAsync(string connectionString, Guid tripId)
    {
        await using var db = Context(connectionString);
        return await db.RideSagas.AsNoTracking().SingleAsync(s => s.TripId == tripId);
    }

    public static async Task<TripOutboxMessage?> OutboxRowAsync(string connectionString, string messageId)
    {
        await using var db = Context(connectionString);
        return await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(m => m.MessageId == messageId);
    }

    public static async Task<int> OutboxCountAsync(string connectionString, Guid tripId)
    {
        await using var db = Context(connectionString);
        return await db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.AggregateId == tripId.ToString("D"));
    }

    public static async Task<TripMessageReceipt?> ReceiptAsync(string connectionString, string messageId)
    {
        await using var db = Context(connectionString);
        return await db.MessageReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.MessageId == messageId);
    }

    // ---------------------------------------------------------- broker access

    public static async Task PurgeQueuesAsync(ConnectionFactory factory, params string[] queues)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in queues)
        {
            await channel.QueuePurgeAsync(queue);
        }
    }

    public static async Task<uint> QueueMessageCountAsync(ConnectionFactory factory, string queue)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    public static async Task<uint> QueueConsumerCountAsync(ConnectionFactory factory, string queue)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.ConsumerCountAsync(queue);
    }

    /// <summary>Removes every currently queued message (the harness is the
    /// only consumer of command queues in these scenarios).</summary>
    public static async Task<List<MessageEnvelope<JsonElement>>> DrainQueueAsync(
        ConnectionFactory factory, string queue)
    {
        var envelopes = new List<MessageEnvelope<JsonElement>>();
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } delivery)
        {
            envelopes.Add(JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(delivery.Body.ToArray())!);
        }

        return envelopes;
    }

    public static async Task PublishAsync(
        ConnectionFactory factory, string routingKey, MessageEnvelope<JsonElement> envelope)
    {
        await using var publisher = new ConfirmedPublisher(factory);
        await publisher.PublishAsync(RideTopology.EventsExchange, routingKey, envelope);
    }

    // ---------------------------------------------------------- authority envelopes

    private static MessageEnvelope<JsonElement> DriverEvent(
        string messageType, Guid tripId, Guid driverId, Guid reservationId) =>
        MessageEnvelope<JsonElement>.Create(messageType, tripId.ToString("D"),
            JsonSerializer.SerializeToElement(new { tripId, driverId, reservationId }),
            aggregateType: "Driver", aggregateId: driverId.ToString("D"));

    public static MessageEnvelope<JsonElement> DriverReserved(Guid tripId, Guid driverId, Guid reservationId,
        int round = 1) =>
        MessageEnvelope<JsonElement>.Create("DriverReservedV1", tripId.ToString("D"),
            JsonSerializer.SerializeToElement(new { tripId, driverId, reservationId, matchingRound = round }),
            aggregateType: "Driver", aggregateId: driverId.ToString("D"));

    public static MessageEnvelope<JsonElement> DriverReservationReleased(
        Guid tripId, Guid driverId, Guid reservationId) =>
        DriverEvent("DriverReservationReleasedV1", tripId, driverId, reservationId);

    public static MessageEnvelope<JsonElement> DriverTripStarted(Guid tripId, Guid driverId, Guid reservationId) =>
        DriverEvent("DriverTripStartedV1", tripId, driverId, reservationId);

    public static MessageEnvelope<JsonElement> DriverTripCompleted(Guid tripId, Guid driverId, Guid reservationId) =>
        DriverEvent("DriverTripCompletedV1", tripId, driverId, reservationId);

    private static MessageEnvelope<JsonElement> PaymentOutcome(
        string messageType, Guid tripId, Guid sagaId, string operationKey, string? resultCode) =>
        MessageEnvelope<JsonElement>.Create(messageType, tripId.ToString("D"),
            JsonSerializer.SerializeToElement(new { tripId, reasonCode = resultCode }),
            sagaId: sagaId.ToString("D"), operationKey: operationKey);

    public static MessageEnvelope<JsonElement> PaymentAuthorized(
        Guid tripId, Guid sagaId, string authorizeOperationKey) =>
        PaymentOutcome("PaymentAuthorizedV1", tripId, sagaId, authorizeOperationKey, null);

    public static MessageEnvelope<JsonElement> PaymentAuthorizationDeclined(
        Guid tripId, Guid sagaId, string authorizeOperationKey) =>
        PaymentOutcome("PaymentAuthorizationFailedV1", tripId, sagaId, authorizeOperationKey, "DECLINED");

    public static MessageEnvelope<JsonElement> PaymentAuthorizationVoided(
        Guid tripId, Guid sagaId, string voidOperationKey) =>
        PaymentOutcome("PaymentAuthorizationVoidedV1", tripId, sagaId, voidOperationKey, null);

    public static MessageEnvelope<JsonElement> PaymentCaptured(
        Guid tripId, Guid sagaId, string captureOperationKey) =>
        PaymentOutcome("PaymentCapturedV1", tripId, sagaId, captureOperationKey, null);

    /// <summary>Reads one Guid-valued payload field of a durable intent.</summary>
    public static Guid PayloadGuid(TripOutboxMessage message, string name) =>
        Guid.Parse(JsonDocument.Parse(message.PayloadJson).RootElement.GetProperty(name).GetString()
            ?? throw new InvalidOperationException($"Payload field '{name}' is missing."));
}

/// <summary>One real Trip service OS process launched and owned by the test.</summary>
internal sealed class TripProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _health = new() { Timeout = TimeSpan.FromSeconds(5) };
    public readonly string RunDirectory;
    public int ProcessId => _process.Id;

    private TripProcess(Process process, string runDirectory)
    {
        _process = process;
        RunDirectory = runDirectory;
    }

    public static async Task<TripProcess> StartAsync(
        string connectionString, ConnectionFactory broker, string label,
        string? faultPointSignalDirectory = null, int faultPointWaitSeconds = 120)
    {
        var appHost = ResolveServiceAppHost();
        var port = GetFreePort();
        var runDirectory = Path.Combine(Path.GetTempPath(), $"trip-w11-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["ConnectionStrings__Postgres"] = connectionString,
            ["TripDatabase__AutoMigrate"] = "true",
            ["TripMessaging__Enabled"] = "true",
            ["RabbitMq__Host"] = broker.HostName,
            ["RabbitMq__Port"] = broker.Port.ToString(),
            ["RabbitMq__Username"] = broker.UserName,
            ["RabbitMq__Password"] = broker.Password,
            ["Outbox__PollIntervalMs"] = "100",
            ["Outbox__ClaimLeaseSeconds"] = "2",
            ["Outbox__RetryDelaySeconds"] = "1",
        };
        if (faultPointSignalDirectory is not null)
        {
            environment["TripFaultPoints__Enabled"] = "true";
            environment["TripFaultPoints__SignalDirectory"] = faultPointSignalDirectory;
            environment["TripFaultPoints__Activated__0"] =
                RideHailing.TripService.Integration.FaultPoints.TripFaultPointNames.SagaAfterPersistBeforeDispatch;
            environment["TripFaultPoints__WaitTimeoutSeconds"] = faultPointWaitSeconds.ToString();
        }

        var dotnetRoot = ResolveDotNetRoot();
        if (dotnetRoot is not null)
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = appHost,
            WorkingDirectory = ResolveRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Trip service process.");
        var trip = new TripProcess(process, runDirectory);
        var stdout = Path.Combine(runDirectory, "stdout.log");
        var stderr = Path.Combine(runDirectory, "stderr.log");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) File.AppendAllText(stdout, e.Data + "\n"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) File.AppendAllText(stderr, e.Data + "\n"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await TripW11Wait.UntilAsync($"Trip process {process.Id} /health/ready",
                async () =>
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"Trip process {process.Id} exited early with code {process.ExitCode}.\n" +
                            Tail(stderr, 4000));
                    }

                    try
                    {
                        using var response = await trip._health.GetAsync($"http://127.0.0.1:{port}/health/ready");
                        return response.IsSuccessStatusCode;
                    }
                    catch (HttpRequestException) { return false; }
                }, TimeSpan.FromSeconds(90));
        }
        catch
        {
            await trip.DisposeAsync();
            throw;
        }

        return trip;
    }

    /// <summary>
    /// Abrupt termination (SIGKILL on Linux) of exactly this owned process
    /// tree, verified to have exited. This is the crash mechanism the W11
    /// proofs require — a graceful stop or a disposed DbContext is not a
    /// process crash.
    /// </summary>
    public async Task KillAsync()
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"Trip process {ProcessId} had already exited (code {_process.ExitCode}) before the crash step.");
        }

        _process.Kill(entireProcessTree: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"Trip process {ProcessId} did not exit after SIGKILL.");
        }

        if (!_process.HasExited)
        {
            throw new InvalidOperationException($"Trip process {ProcessId} survived SIGKILL.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await _process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { }
            }
            catch { /* already gone */ }
        }

        _process.Dispose();
        _health.Dispose();
    }

    private static string ResolveServiceAppHost()
    {
        var repoRoot = ResolveRepoRoot();
        var executable = OperatingSystem.IsWindows() ? "RideHailing.TripService.exe" : "RideHailing.TripService";
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(repoRoot, "src", "Services", "Trip", "RideHailing.TripService",
                "bin", configuration, "net10.0", executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Trip service apphost not found under src/Services/Trip/RideHailing.TripService/bin/. " +
            "Build the solution first (dotnet build RideHailing.Distributed.slnx -c Release).");
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RideHailing.Distributed.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root (RideHailing.Distributed.slnx) not found.");
    }

    /// <summary>
    /// The apphost resolves the runtime through DOTNET_ROOT; derive it from
    /// the test host's own runtime directory for SDK layouts that are not on
    /// PATH. Harmless where a system-wide install already resolves.
    /// </summary>
    private static string? ResolveDotNetRoot()
    {
        var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (!runtimeDirectory.Contains("shared", StringComparison.Ordinal))
        {
            return null;
        }

        return new DirectoryInfo(runtimeDirectory).Parent?.Parent?.Parent?.FullName;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static string Tail(string path, int maxChars)
    {
        if (!File.Exists(path))
        {
            return "(no stderr captured)";
        }

        var text = File.ReadAllText(path);
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
