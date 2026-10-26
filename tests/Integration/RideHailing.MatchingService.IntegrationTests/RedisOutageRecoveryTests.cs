// B-W11-08 (REL-14): a real disposable Redis container is network-isolated
// through a test-controlled TCP gate. Closing the gate drops every client
// connection; the production candidate path must degrade without fabricating
// candidates or reservations. Reopening the gate proves reconnect. Redis
// process/data remain alive, so survival is proven separately from
// authoritative-event reconstruction (which is exercised for a second driver).
// There is no automatic full projection rebuild in the implementation.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.MatchingService.Application.Matching;
using RideHailing.MatchingService.Application.Messaging;
using RideHailing.MatchingService.Application.Projection;
using RideHailing.MatchingService.Domain.Matching;
using RideHailing.MatchingService.Infrastructure.Persistence;
using RideHailing.Messaging.Contracts;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class RedisOutageRecoveryTests
{
    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;
    private readonly ITestOutputHelper _output;

    public RedisOutageRecoveryTests(
        MatchingPostgresDatabase matching, DriverTestDatabase drivers, ITestOutputHelper output)
    {
        _matching = matching;
        _drivers = drivers;
        _output = output;
    }

    private static MessageEnvelope<JsonElement> Envelope(string type, object payload) =>
        MessageEnvelope<JsonElement>.Create(
            type, "w11-REL14-" + Guid.NewGuid().ToString("N"),
            JsonSerializer.SerializeToElement(payload));

    private static Task<HandlerOutcome> DeliverAsync(
        DriverProjectionHandler handler, string route, MessageEnvelope<JsonElement> envelope) =>
        handler.HandleAsync(new BrokerDelivery(
            route,
            JsonSerializer.SerializeToUtf8Bytes(envelope),
            new Dictionary<string, object?>(),
            0,
            false), default);

    private static async Task WaitForSharedPingAsync(
        SharedRedisMultiplexer shared, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var db = await shared.GetDatabaseAsync(deadline.Token);
                await db.PingAsync();
                return;
            }
            catch (Exception) when (!deadline.IsCancellationRequested)
            {
                await Task.Delay(100, deadline.Token);
            }
        }

        throw new TimeoutException("Shared Redis multiplexer did not reconnect.");
    }

    private async Task<Guid> SeedAvailableDriverAsync(CancellationToken ct)
    {
        var repo = _drivers.CreateRepository();
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await repo.TryInsertAsync(driver, ct));
        return driver.DriverId;
    }

    private static async Task ProjectAsync(
        DriverProjectionHandler handler, Guid driverId, int version,
        double lon, double lat, string session, CancellationToken ct)
    {
        var state = Envelope("DriverAvailableV1", new
        {
            driverId, status = "AVAILABLE", version,
        });
        var location = Envelope("DriverLocationAcceptedV1", new
        {
            driverId, longitude = lon, latitude = lat, sessionId = session,
            sequenceNo = 1, capturedAt = DateTimeOffset.UtcNow,
        });
        Assert.Equal(HandlerOutcome.Done,
            await DeliverAsync(handler, "driver.available.v1", state));
        Assert.Equal(HandlerOutcome.Done,
            await DeliverAsync(handler, "driver.location-accepted.v1", location));
        ct.ThrowIfCancellationRequested();
    }

    [Fact]
    public async Task RedisPartition_DegradesCandidatePath_ThenSurvivalAndEventReconstruction()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        var elapsed = Stopwatch.StartNew();
        var redis = new RedisBuilder("redis:8.10.2")
            .WithAutoRemove(true)
            .Build();
        await redis.StartAsync(ct);
        TcpGate? gate = null;
        try
        {
            var redisEndpoint = ConfigurationOptions.Parse(redis.GetConnectionString())
                .EndPoints.First();
            var (upstreamHost, upstreamPort) = redisEndpoint switch
            {
                DnsEndPoint dns => (dns.Host, dns.Port),
                IPEndPoint ip => (ip.Address.ToString(), ip.Port),
                _ => throw new InvalidOperationException(
                    $"Unsupported Redis endpoint {redisEndpoint}."),
            };
            gate = new TcpGate(upstreamHost, upstreamPort);
            gate.Start();

            await using var seedMux = await RedisProjectionUpdater.ConnectAsync(redis.GetConnectionString());
            var seedUpdater = new RedisProjectionUpdater(seedMux);
            await seedUpdater.EnsureFunctionAsync();
            var seedHandler = new DriverProjectionHandler(
                seedUpdater, NullLogger<DriverProjectionHandler>.Instance);

            var driverSurvives = await SeedAvailableDriverAsync(ct);
            var driverRebuilt = await SeedAvailableDriverAsync(ct);
            var repo = _drivers.CreateRepository();
            var survivesBefore = (await repo.LoadAsync(driverSurvives, ct))!;
            var rebuiltBefore = (await repo.LoadAsync(driverRebuilt, ct))!;
            const double lon = 106.6;
            const double lat = 10.8;
            await ProjectAsync(seedHandler, driverSurvives, survivesBefore.Version,
                lon, lat, "survives", ct);
            await ProjectAsync(seedHandler, driverRebuilt, rebuiltBefore.Version,
                lon + 0.0001, lat + 0.0001, "rebuilt", ct);

            await using var shared = new SharedRedisMultiplexer($"127.0.0.1:{gate.Port}");
            var matchingRepo = new MatchingRepository(_matching.ConnectionString);
            var service = new MatchingRoundService(
                matchingRepo,
                new CandidateDiscovery(token => shared.GetDatabaseAsync(token)),
                new MatchingMetrics(),
                TimeProvider.System);
            var policy = new MatchingPolicy
            {
                SearchRadiusKm = 5,
                MaxCandidates = 10,
                DiscoveryOversample = 2,
                DiscoveryHardCap = 20,
                LocationFreshnessWindow = TimeSpan.FromMinutes(10),
            };

            var beforeTrip = Guid.NewGuid();
            var before = await service.ComputeAsync(
                new ComputeRoundRequest(beforeTrip, 1, lon, lat), policy, ct);
            Assert.False(before.Degraded);
            Assert.Contains(before.Candidates, c => c.DriverId == driverSurvives);
            Assert.Contains(before.Candidates, c => c.DriverId == driverRebuilt);

            await gate.StopAsync();

            var outageTrip = Guid.NewGuid();
            ComputeRoundResult outage;
            try
            {
                outage = await service.ComputeAsync(
                    new ComputeRoundRequest(outageTrip, 1, lon, lat), policy, ct);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Production candidate path threw instead of degrading: {ex}");
                throw;
            }

            Assert.True(outage.Degraded);
            Assert.Empty(outage.Candidates);
            var attempts = new MatchingAttemptRepository(_matching.ConnectionString);
            Assert.Empty(await attempts.LoadAttemptsAsync(outageTrip, 1, ct));
            var survivesDuring = (await repo.LoadAsync(driverSurvives, ct))!;
            var rebuiltDuring = (await repo.LoadAsync(driverRebuilt, ct))!;
            Assert.Equal(survivesBefore.Status, survivesDuring.Status);
            Assert.Equal(survivesBefore.Version, survivesDuring.Version);
            Assert.Equal(survivesBefore.ReservationId, survivesDuring.ReservationId);
            Assert.Equal(rebuiltBefore.Status, rebuiltDuring.Status);
            Assert.Equal(rebuiltBefore.Version, rebuiltDuring.Version);
            Assert.Equal(rebuiltBefore.ReservationId, rebuiltDuring.ReservationId);

            gate.Start();
            await WaitForSharedPingAsync(shared, ct);

            bool survived;
            bool rebuilt;
            await using (var verifyMux = await RedisProjectionUpdater.ConnectAsync(redis.GetConnectionString()))
            {
                var db = verifyMux.GetDatabase();
                survived = await db.KeyExistsAsync(RedisProjectionKeys.ProjectionKey(driverSurvives));
                rebuilt = await db.KeyExistsAsync(RedisProjectionKeys.ProjectionKey(driverRebuilt));
            }

            Assert.True(survived);
            Assert.True(rebuilt);
            var recoveryTrip = Guid.NewGuid();
            var recovered = await service.ComputeAsync(
                new ComputeRoundRequest(recoveryTrip, 1, lon, lat), policy, ct);
            Assert.False(recovered.Degraded);
            Assert.Contains(recovered.Candidates, c => c.DriverId == driverSurvives);
            Assert.Contains(recovered.Candidates, c => c.DriverId == driverRebuilt);

            // Reconstruction path: drop one projection and replay
            // authoritative Driver state/location events through the real
            // handler. This is supported event-driven recovery, not an
            // automatic full rebuild.
            await using (var deleteMux = await RedisProjectionUpdater.ConnectAsync(redis.GetConnectionString()))
            {
                var db = deleteMux.GetDatabase();
                await db.KeyDeleteAsync(RedisProjectionKeys.ProjectionKey(driverRebuilt));
                await db.SortedSetRemoveAsync(
                    RedisProjectionKeys.GeoKey, RedisProjectionKeys.Member(driverRebuilt));
            }

            var afterDelete = await service.ComputeAsync(
                new ComputeRoundRequest(Guid.NewGuid(), 1, lon, lat), policy, ct);
            Assert.DoesNotContain(afterDelete.Candidates, c => c.DriverId == driverRebuilt);

            await using (var replayMux = await RedisProjectionUpdater.ConnectAsync(redis.GetConnectionString()))
            {
                var replayUpdater = new RedisProjectionUpdater(replayMux);
                await replayUpdater.EnsureFunctionAsync();
                var replayHandler = new DriverProjectionHandler(
                    replayUpdater, NullLogger<DriverProjectionHandler>.Instance);
                await ProjectAsync(replayHandler, driverRebuilt, rebuiltBefore.Version,
                    lon + 0.0001, lat + 0.0001, "rebuilt-replay", ct);
            }

            var afterReplay = await service.ComputeAsync(
                new ComputeRoundRequest(Guid.NewGuid(), 1, lon, lat), policy, ct);
            Assert.Contains(afterReplay.Candidates, c => c.DriverId == driverRebuilt);

            var survivesAfter = (await repo.LoadAsync(driverSurvives, ct))!;
            var rebuiltAfter = (await repo.LoadAsync(driverRebuilt, ct))!;
            Assert.Equal(survivesBefore.Status, survivesAfter.Status);
            Assert.Equal(survivesBefore.Version, survivesAfter.Version);
            Assert.Equal(survivesBefore.ReservationId, survivesAfter.ReservationId);
            Assert.Equal(rebuiltBefore.Status, rebuiltAfter.Status);
            Assert.Equal(rebuiltBefore.Version, rebuiltAfter.Version);
            Assert.Equal(rebuiltBefore.ReservationId, rebuiltAfter.ReservationId);

            _output.WriteLine(
                $"W11 REL-14 driverSurvives={driverSurvives} driverRebuilt={driverRebuilt} " +
                $"outageTrip={outageTrip} partition=closed degraded=true candidates=0 " +
                $"attempts=0 driverUnchanged=true survived={survived} eventReplayReconstructed=true " +
                $"recovered=true elapsedMs={elapsed.ElapsedMilliseconds}");
        }
        finally
        {
            if (gate is not null)
            {
                await gate.DisposeAsync();
            }

            await redis.DisposeAsync();
        }
    }

    /// <summary>
    /// Test-only TCP gate: forwards Redis traffic to the real disposable
    /// container. Stopping it closes the listener and every active connection,
    /// which partitions the candidate path without touching the Redis process.
    /// </summary>
    private sealed class TcpGate : IAsyncDisposable
    {
        private readonly string _upstreamHost;
        private readonly int _upstreamPort;
        private readonly object _clientsGate = new();
        private readonly List<TcpClient> _clients = [];
        private TcpListener? _listener;
        private CancellationTokenSource? _stopping;
        private Task? _acceptLoop;

        public TcpGate(string upstreamHost, int upstreamPort)
        {
            _upstreamHost = upstreamHost;
            _upstreamPort = upstreamPort;
        }

        public int Port { get; private set; }

        public void Start()
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException("Gate is already running.");
            }

            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Server.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _stopping = new CancellationTokenSource();
            _acceptLoop = AcceptLoopAsync(_stopping.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(ct);
                }
                catch (Exception) when (ct.IsCancellationRequested
                    || _listener is null)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = ProxyAsync(client, ct);
            }
        }

        private async Task ProxyAsync(TcpClient client, CancellationToken ct)
        {
            lock (_clientsGate)
            {
                _clients.Add(client);
            }

            try
            {
                using (client)
                using (var upstream = new TcpClient())
                {
                    await upstream.ConnectAsync(_upstreamHost, _upstreamPort, ct);
                    var clientStream = client.GetStream();
                    var upstreamStream = upstream.GetStream();
                    var forward = clientStream.CopyToAsync(upstreamStream, ct);
                    var backward = upstreamStream.CopyToAsync(clientStream, ct);
                    await Task.WhenAny(forward, backward);
                }
            }
            catch (Exception)
            {
                // Partition/teardown closes both ends.
            }
            finally
            {
                lock (_clientsGate)
                {
                    _clients.Remove(client);
                }
            }
        }

        public async Task StopAsync()
        {
            if (_listener is null)
            {
                return;
            }

            _stopping?.Cancel();
            _listener.Stop();
            lock (_clientsGate)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }

                _clients.Clear();
            }

            if (_acceptLoop is not null)
            {
                try
                {
                    await _acceptLoop;
                }
                catch (Exception)
                {
                    // Expected during teardown.
                }
            }

            _listener = null;
            _acceptLoop = null;
            _stopping?.Dispose();
            _stopping = null;
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
