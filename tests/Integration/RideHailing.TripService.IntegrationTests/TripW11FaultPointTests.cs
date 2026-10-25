// A-W11-01 faultpoint proofs: settings binding, gate semantics and the
// registration wiring (Doc 05A §19.2, Doc 02 §27).
//
// WHAT IS PROVEN HERE (in-process, container-free):
//   * faultpoints are disabled by default and double-gated (config + non-Production
//     host environment; an explicit enable in Production fails startup);
//   * misconfiguration (missing signal directory, unknown name, unbounded
//     timeout) fails loudly instead of silently disabling a proof;
//   * the gate holds ONLY activated saga dispatches, publishes the observable
//     "reached" marker, releases on the harness signal and never blocks past
//     its deadline;
//   * the relay registration wires the pre-dispatch seam exactly when
//     faultpoints are enabled and leaves it null otherwise.
//
// The real producer-process crash/restart behavior of this faultpoint is
// proven by TripW11FaultProcessTests (REL-04/07/17, Doc 03 §8).

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RideHailing.Messaging.Contracts;
using RideHailing.Messaging.Outbox;
using RideHailing.TripService.Integration.FaultPoints;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

public sealed class TripW11FaultPointTests
{
    private static TripFaultPointSettings Settings(
        string signalDirectory, int waitSeconds = 30, int pollMs = 10,
        string[]? activated = null) => new()
    {
        Enabled = true,
        SignalDirectory = signalDirectory,
        Activated = activated ?? [TripFaultPointNames.SagaAfterPersistBeforeDispatch],
        WaitTimeoutSeconds = waitSeconds,
        PollIntervalMs = pollMs,
    };

    // ------------------------------------------------------------- settings

    [Fact]
    public void Settings_DefaultToDisabled()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var settings = TripFaultPointSettings.FromConfiguration(config);
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void Settings_EnabledWithoutSignalDirectory_FailLoudly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TripFaultPoints:Enabled"] = "true",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => TripFaultPointSettings.FromConfiguration(config));
    }

    [Fact]
    public void Settings_UnknownActivatedName_FailLoudly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TripFaultPoints:Enabled"] = "true",
            ["TripFaultPoints:SignalDirectory"] = Path.GetTempPath(),
            ["TripFaultPoints:Activated:0"] = "Not.A.FaultPoint",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => TripFaultPointSettings.FromConfiguration(config));
    }

    [Fact]
    public void Settings_UnboundedOrMissingWaitTimeout_FailLoudly()
    {
        IConfiguration Base(string? timeout) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TripFaultPoints:Enabled"] = "true",
                ["TripFaultPoints:SignalDirectory"] = Path.GetTempPath(),
                ["TripFaultPoints:Activated:0"] = TripFaultPointNames.SagaAfterPersistBeforeDispatch,
                ["TripFaultPoints:WaitTimeoutSeconds"] = timeout,
            }).Build();

        Assert.Throws<InvalidOperationException>(() => TripFaultPointSettings.FromConfiguration(Base("0")));
        Assert.Throws<InvalidOperationException>(() => TripFaultPointSettings.FromConfiguration(Base("601")));
        Assert.NotNull(TripFaultPointSettings.FromConfiguration(Base(null))); // default 30s, bounded.
    }

    [Fact]
    public void Settings_BindCanonicalName()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TripFaultPoints:Enabled"] = "true",
            ["TripFaultPoints:SignalDirectory"] = Path.GetTempPath(),
            ["TripFaultPoints:Activated:0"] = TripFaultPointNames.SagaAfterPersistBeforeDispatch,
            ["TripFaultPoints:WaitTimeoutSeconds"] = "45",
        }).Build();
        var settings = TripFaultPointSettings.FromConfiguration(config);
        Assert.True(settings.Enabled);
        Assert.Equal(45, settings.WaitTimeoutSeconds);
        Assert.Equal(TripFaultPointNames.SagaAfterPersistBeforeDispatch, Assert.Single(settings.Activated));
    }

    // ---------------------------------------------------------------- gate

    [Fact]
    public async Task Gate_HoldsSagaDispatchUntilReleased_AndWritesObservableReachedMarker()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"w11-gate-{Guid.NewGuid():N}");
        var gate = new TripFaultPointGate(Settings(directory), NullLogger<TripFaultPointGate>.Instance);
        var name = TripFaultPointNames.SagaAfterPersistBeforeDispatch;
        var reached = TripFaultPointGate.ReachedPath(directory, name);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var held = gate.HoldBeforePublishAsync(name, "AuthorizePaymentV1", "trip:proof:authorize:v1", cts.Token);
        await TripW11Wait.UntilAsync("reached marker", () => Task.FromResult(File.Exists(reached)),
            TimeSpan.FromSeconds(5));

        // Still held (no release): the dispatch has not been allowed to continue.
        Assert.False(held.IsCompleted);

        var marker = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(reached));
        Assert.Equal(name, marker.GetProperty("faultPoint").GetString());
        Assert.Equal("trip:proof:authorize:v1", marker.GetProperty("messageId").GetString());
        Assert.Equal("AuthorizePaymentV1", marker.GetProperty("messageType").GetString());
        Assert.Equal(Environment.ProcessId, marker.GetProperty("processId").GetInt32());

        File.WriteAllText(TripFaultPointGate.ReleasePath(directory, name), "release");
        await held.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Gate_IgnoresNonSagaDispatchesAndUnactivatedGates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"w11-gate-{Guid.NewGuid():N}");
        var name = TripFaultPointNames.SagaAfterPersistBeforeDispatch;
        var gate = new TripFaultPointGate(Settings(directory), NullLogger<TripFaultPointGate>.Instance);

        // Non-saga dispatches are outside this faultpoint's scope by name.
        await gate.HoldBeforePublishAsync(name, "ReleaseDriverReservationV1", "trip:proof:release:v1",
            CancellationToken.None);
        Assert.False(File.Exists(TripFaultPointGate.ReachedPath(directory, name)));

        // A saga dispatch is also ignored when the faultpoint is not activated.
        var unactivated = new TripFaultPointGate(Settings(directory, activated: []),
            NullLogger<TripFaultPointGate>.Instance);
        await unactivated.HoldBeforePublishAsync(name, "AuthorizePaymentV1", "trip:proof:authorize:v1",
            CancellationToken.None);
        Assert.False(File.Exists(TripFaultPointGate.ReachedPath(directory, name)));

        // Disabled settings never gate anything.
        var disabled = new TripFaultPointGate(TripFaultPointSettings.Disabled,
            NullLogger<TripFaultPointGate>.Instance);
        await disabled.HoldBeforePublishAsync(name, "CapturePaymentV1", "trip:proof:capture:v1",
            CancellationToken.None);
    }

    [Fact]
    public async Task Gate_NeverBlocksPastItsDeadline()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"w11-gate-{Guid.NewGuid():N}");
        var gate = new TripFaultPointGate(Settings(directory, waitSeconds: 1),
            NullLogger<TripFaultPointGate>.Instance);
        var name = TripFaultPointNames.SagaAfterPersistBeforeDispatch;

        // No release signal is ever written: the bounded wait must expire.
        await gate.HoldBeforePublishAsync(name, "AuthorizePaymentV1", "trip:proof:authorize:v1",
            CancellationToken.None);
        Assert.True(File.Exists(TripFaultPointGate.ReachedPath(directory, name)));
    }

    // -------------------------------------------------------- registration wiring

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "trip-tests";
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration MessagingConfig(string? faultPointDirectory = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["TripMessaging:Enabled"] = "true",
            ["RabbitMq:Host"] = "127.0.0.1",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:Username"] = "wiring-proof",
            ["RabbitMq:Password"] = "wiring-proof",
        };
        if (faultPointDirectory is not null)
        {
            values["TripFaultPoints:Enabled"] = "true";
            values["TripFaultPoints:SignalDirectory"] = faultPointDirectory;
            values["TripFaultPoints:Activated:0"] = TripFaultPointNames.SagaAfterPersistBeforeDispatch;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task Registration_WithoutFaultPoints_LeavesTheRelaySeamOff()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTripMessaging(MessagingConfig(), new TestHostEnvironment("Development"));
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<OutboxRelayOptions>();
        Assert.Null(options.BeforePublish);
        Assert.Null(options.AfterPublishBeforeMark);
    }

    [Fact]
    public async Task Registration_WithFaultPoints_WiresThePreDispatchSeam()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"w11-wiring-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTripMessaging(MessagingConfig(directory), new TestHostEnvironment("Development"));
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<OutboxRelayOptions>();
        Assert.NotNull(options.BeforePublish);

        // The wired seam resolves the gate and is inert for a non-saga dispatch.
        var nonSaga = OutboxMessage.Create(
            MessageEnvelope<JsonElement>.Create("ReleaseDriverReservationV1", "wiring-proof",
                JsonSerializer.SerializeToElement(new { proof = true })),
            "ride.commands", "driver.release-reservation.v1");
        await options.BeforePublish!(nonSaga, CancellationToken.None);
        var name = TripFaultPointNames.SagaAfterPersistBeforeDispatch;
        Assert.False(File.Exists(TripFaultPointGate.ReachedPath(directory, name)));
    }

    [Fact]
    public void Registration_RefusesFaultPointsInTheProductionEnvironment()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"w11-prod-{Guid.NewGuid():N}");
        Assert.Throws<InvalidOperationException>(() =>
        {
            var services = new ServiceCollection();
            services.AddTripMessaging(MessagingConfig(directory), new TestHostEnvironment("Production"));
        });
    }
}
