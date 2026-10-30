// L3: demo/test-origin Matching state must never become production work.
//
// The demo compute seam creates a PROCESSING round with auto_execute = FALSE
// and caller-selected pickup/candidate identities. Canonical RideRequestedV1
// intake (auto_execute = TRUE) may only converge with a production-created
// row; adopting demo state would make the production worker reserve using
// non-canonical data. These proofs use the real repositories on real
// PostgreSQL and assert the absence of Driver/Outbox effects.
using Npgsql;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.MatchingService.Infrastructure.Persistence;

namespace RideHailing.MatchingService.IntegrationTests;

[Collection(ReserveWorkflowCollection.Name)]
public sealed class ProductionOriginIsolationTests
{
    private readonly MatchingPostgresDatabase _matching;
    private readonly DriverTestDatabase _drivers;

    public ProductionOriginIsolationTests(
        MatchingPostgresDatabase matching,
        DriverTestDatabase drivers)
    {
        _matching = matching;
        _drivers = drivers;
    }

    private MatchingRepository DemoRepository() => new(_matching.ConnectionString);

    private MatchingAttemptRepository Attempts() => new(_matching.ConnectionString);

    private async Task<Guid> SeedAvailableDriverAsync()
    {
        var driver = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        driver.GoOnline(driver.Version);
        Assert.True(await _drivers.CreateRepository().TryInsertAsync(driver));
        return driver.DriverId;
    }

    private async Task<long> DriverReservedOutboxAsync(Guid driver) =>
        await CountAsync(
            _drivers.DataSource,
            "SELECT count(*) FROM outbox_messages WHERE message_type = 'DriverReservedV1' AND aggregate_id = @id",
            ("id", driver.ToString("N")));

    private Task<long> MatchingOutboxAsync(Guid trip) => CountAsync(
        _matching.DataSource,
        "SELECT count(*) FROM outbox_messages WHERE payload_json->>'tripId' = @trip",
        ("trip", trip.ToString("D")));

    private static async Task<long> CountAsync(
        NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task DemoFirst_ProductionIntake_RejectsContamination_WithoutAnyEffect()
    {
        var trip = Guid.NewGuid();
        var driver = await SeedAvailableDriverAsync();
        var demoReservation = Guid.NewGuid();
        var demoPickup = (Longitude: 1.25, Latitude: 2.5);
        var productionPickup = (Longitude: 106.7, Latitude: 10.8);

        // Demo seam shape: round + caller-selected READY attempt, no
        // auto-execute obligation.
        var demo = DemoRepository();
        await demo.InsertRoundAsync(
            trip, 1, $"demo:{trip:N}", "v1", demoPickup.Longitude, demoPickup.Latitude);
        await demo.InsertAttemptAsync(trip, 1, 0, driver, demoReservation);

        var attempts = Attempts();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attempts.EnsureProductionRoundAsync(
                trip, 1, $"prod:{trip:N}", productionPickup.Longitude, productionPickup.Latitude));
        Assert.Contains("MATCHING_ROUND_ORIGIN_CONFLICT", error.Message, StringComparison.Ordinal);

        var round = (await demo.LoadRoundAsync(trip, 1))!;
        Assert.False(round.AutoExecute);
        Assert.Equal($"demo:{trip:N}", round.InputMessageId);
        Assert.Null(round.SnapshotHash);
        Assert.Null(round.SnapshotJson);
        var durable = (await Attempts().LoadRoundAsync(trip, 1))!;
        Assert.Equal(demoPickup.Longitude, durable.PickupLongitude);
        Assert.Equal(demoPickup.Latitude, durable.PickupLatitude);

        var ledger = await demo.LoadAttemptsAsync(trip, 1);
        var attempt = Assert.Single(ledger);
        Assert.Equal(driver, attempt.DriverId);
        Assert.Equal(demoReservation, attempt.ReservationId);
        Assert.Equal("READY", attempt.State);

        // No execution authority and no side effect of any kind.
        var due = await new MatchingWorkRepository(_matching.ConnectionString)
            .ListDueRoundsAsync(DateTimeOffset.UtcNow.AddMinutes(1), 100);
        Assert.DoesNotContain(due, row => row.TripId == trip && row.Round == 1);
        var driverRow = (await _drivers.CreateRepository().LoadAsync(driver))!;
        Assert.Equal(DriverStatus.Available, driverRow.Status);
        Assert.Null(driverRow.ReservationId);
        Assert.Equal(0L, await DriverReservedOutboxAsync(driver));
        Assert.Equal(0L, await MatchingOutboxAsync(trip));
    }

    [Fact]
    public async Task ProductionFirst_DuplicateIntake_Converges_WithoutDemoAdoption()
    {
        var trip = Guid.NewGuid();
        var attempts = Attempts();
        var first = await attempts.EnsureProductionRoundAsync(
            trip, 1, $"prod-one:{trip:N}", 106.7, 10.8);
        Assert.True(first.AutoExecute);
        Assert.Equal($"prod-one:{trip:N}", first.InputMessageId);

        // Exact duplicate delivery converges on the same durable row.
        var replay = await attempts.EnsureProductionRoundAsync(
            trip, 1, $"prod-one:{trip:N}", 106.7, 10.8);
        Assert.Equal(first.InputMessageId, replay.InputMessageId);
        Assert.True(replay.AutoExecute);

        // A late delivery with a different envelope identity converges too;
        // the first production identity is never overwritten.
        var delayed = await attempts.EnsureProductionRoundAsync(
            trip, 1, $"prod-two:{trip:N}", 106.9, 10.9);
        Assert.Equal(first.InputMessageId, delayed.InputMessageId);
        Assert.True(delayed.AutoExecute);
        var all = await Attempts().LoadAttemptsAsync(trip, 1);
        Assert.Empty(all);
    }

    [Fact]
    public async Task DemoCompute_RemainsTestable_WhenNoProductionIntakeExists()
    {
        var trip = Guid.NewGuid();
        var driver = await SeedAvailableDriverAsync();
        var reservation = Guid.NewGuid();
        var demo = DemoRepository();
        await demo.InsertRoundAsync(trip, 1, $"demo:{trip:N}", "v1", 5.0, 6.0);
        await demo.InsertAttemptAsync(trip, 1, 0, driver, reservation);

        var round = (await demo.LoadRoundAsync(trip, 1))!;
        Assert.False(round.AutoExecute);
        Assert.Equal("PROCESSING", round.Status);
        var attempt = Assert.Single(await demo.LoadAttemptsAsync(trip, 1));
        Assert.Equal("READY", attempt.State);
        Assert.Equal(reservation, attempt.ReservationId);
    }
}
