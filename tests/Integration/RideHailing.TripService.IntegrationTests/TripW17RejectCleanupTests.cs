// DS03 Driver Reject integration proofs over real PostgreSQL (Doc 01 §40.1):
// crash-after-commit replay, accept/cancel races, late-event monotonicity and
// Driver-authority release reconciliation. No mock replaces the database
// behavior that these guarantees depend on.

using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RideHailing.TripService.Application.Trips.Lifecycle;
using RideHailing.TripService.Domain.Trips;
using RideHailing.TripService.Infrastructure.Persistence;
using RideHailing.TripService.Integration.Messaging;

namespace RideHailing.TripService.IntegrationTests;

[Collection(TripPostgresCollection.Name)]
public sealed class TripW17RejectCleanupTests(TripPostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private TripDbContext Db(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<TripDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptors).Options);

    private async Task<Trip> SeedReserved(Guid driver, Guid reservation)
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var trip = Trip.Create(Guid.NewGuid(), Guid.NewGuid(), new(10.7, 106.6), new(10.8, 106.7),
            new("VND", 50000m, "ds03", Now), Now);
        trip.StartMatching(Now);
        trip.ApplyDriverReserved(driver, reservation, 1, Now);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        return trip;
    }

    private async Task<TripLifecycleResult> Reject(Trip trip, Guid driver, Guid reservation)
    {
        await using var db = Db();
        return await new RecordDriverRejectedHandler(db, new Clock(),
            NullLogger<RecordDriverRejectedHandler>.Instance)
            .HandleAsync(trip.TripId, driver, reservation);
    }

    private async Task<Trip> Reload(Guid tripId)
    {
        await using var db = Db();
        return await db.Trips.AsNoTracking().SingleAsync(t => t.TripId == tripId);
    }

    private async Task<int> OutboxCount(Guid tripId, string messageType)
    {
        await using var db = Db();
        return await db.OutboxMessages.CountAsync(m =>
            m.AggregateId == tripId.ToString("D") && m.MessageType == messageType);
    }

    [Fact]
    public async Task CommitResponseLost_RetryReplaysWithoutSecondObligation()
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);

        await using (var db = Db(new LostCommitResponse()))
        {
            var handler = new RecordDriverRejectedHandler(db, new Clock(),
                NullLogger<RecordDriverRejectedHandler>.Instance);
            await Assert.ThrowsAsync<IOException>(() =>
                handler.HandleAsync(trip.TripId, driver, reservation));
        }

        var retried = await Reject(trip, driver, reservation);

        Assert.True(retried.IsSuccess);
        Assert.True(retried.IsReplay);
        var final = await Reload(trip.TripId);
        Assert.Equal(TripState.MatchFailed, final.State);
        Assert.Equal(TripFailureReason.DriverRejected, final.FailureReason);
        Assert.Equal(2, final.CleanupIntents.Count);
        Assert.Equal(1, await OutboxCount(trip.TripId, "ReleaseDriverReservationV1"));
        Assert.Equal(1, await OutboxCount(trip.TripId, "CancelMatchingRoundV1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectVsAccept_OneWriterWinsWithoutContradictoryTerminal(bool rejectFirst)
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);

        await using var rejecting = Db();
        await using var accepting = Db();
        var rejectingTrip = await rejecting.Trips.SingleAsync(t => t.TripId == trip.TripId);
        var acceptingTrip = await accepting.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.True(rejectingTrip.RecordDriverRejected(driver, reservation, Now).IsSuccess);
        Assert.True(acceptingTrip.RecordDriverAccepted(driver, reservation, Now).IsSuccess);

        if (rejectFirst)
        {
            await rejecting.SaveChangesAsync();
            await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => accepting.SaveChangesAsync());
        }
        else
        {
            await accepting.SaveChangesAsync();
            await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => rejecting.SaveChangesAsync());
        }

        var final = await Reload(trip.TripId);
        if (rejectFirst)
        {
            Assert.Equal(TripState.MatchFailed, final.State);
            Assert.Equal(TripFailureReason.DriverRejected, final.FailureReason);
            Assert.Null(final.Assignment);
        }
        else
        {
            Assert.Equal(TripState.DriverAccepted, final.State);
            Assert.Null(final.FailureReason);
            Assert.NotNull(final.Assignment);
            Assert.Empty(final.CleanupIntents);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectVsRiderCancel_OneWriterWinsWithOneObligationSet(bool rejectFirst)
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);

        await using var rejecting = Db();
        await using var cancelling = Db();
        var rejectingTrip = await rejecting.Trips.SingleAsync(t => t.TripId == trip.TripId);
        var cancellingTrip = await cancelling.Trips.SingleAsync(t => t.TripId == trip.TripId);
        Assert.True(rejectingTrip.RecordDriverRejected(driver, reservation, Now).IsSuccess);
        Assert.True(cancellingTrip.Cancel(Now).IsSuccess);

        if (rejectFirst)
        {
            await rejecting.SaveChangesAsync();
            await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => cancelling.SaveChangesAsync());
        }
        else
        {
            await cancelling.SaveChangesAsync();
            await Assert.ThrowsAsync<TripConcurrencyConflictException>(() => rejecting.SaveChangesAsync());
        }

        var final = await Reload(trip.TripId);
        Assert.Single(final.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(rejectFirst ? TripState.MatchFailed : TripState.Cancelled, final.State);
        if (rejectFirst)
        {
            Assert.Equal(TripFailureReason.DriverRejected, final.FailureReason);
            Assert.Null(final.Assignment);
        }
    }

    [Fact]
    public async Task LateReservationAndAcceptance_CannotReopenRejectedTrip()
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);
        Assert.True((await Reject(trip, driver, reservation)).IsSuccess);

        await using (var db = Db())
        {
            var replay = await new RecordDriverReservationHandler(db, new Clock(),
                NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(trip.TripId, driver, reservation, 1);
            Assert.True(replay.IsSuccess);
            Assert.True(replay.IsReplay);
        }

        await using (var db = Db())
        {
            var lateOther = await new RecordDriverReservationHandler(db, new Clock(),
                NullLogger<RecordDriverReservationHandler>.Instance)
                .HandleAsync(trip.TripId, Guid.NewGuid(), Guid.NewGuid(), 1);
            Assert.True(lateOther.IsSuccess);
            Assert.False(lateOther.IsReplay);
        }

        await using (var db = Db())
        {
            var acceptance = await new RecordDriverAcceptedHandler(db, new Clock(),
                NullLogger<RecordDriverAcceptedHandler>.Instance)
                .HandleAsync(trip.TripId, driver, reservation);
            Assert.False(acceptance.IsSuccess);
            Assert.Equal(TripErrorCode.TerminalState, acceptance.ErrorCode);
        }

        var final = await Reload(trip.TripId);
        Assert.Equal(TripState.MatchFailed, final.State);
        Assert.Equal(TripFailureReason.DriverRejected, final.FailureReason);
        Assert.Null(final.Assignment);
        // The exact original release plus late leaked-reservation evidence.
        Assert.Equal(2, final.CleanupIntents.Count(i => i.Kind == TripCleanupKind.ReleaseReservation));
    }

    [Theory]
    [InlineData("NOT_COMMITTED_OR_AVAILABLE", true)]
    [InlineData("ReservedByOtherCase", true)]
    [InlineData("OFFLINE", true)]
    [InlineData("NOT_FOUND", true)]
    [InlineData("OWNED_BY_THIS_RESERVATION", false)]
    [InlineData("ON_TRIP", false)]
    public async Task DriverCleanupReconciler_ConfirmsOnlyConclusiveNotOwnedProof(
        string outcome, bool expectCompleted)
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);
        Assert.True((await Reject(trip, driver, reservation)).IsSuccess);
        var wire = outcome == "ReservedByOtherCase" ? "RESERVED_BY_OTHER" : outcome;

        await using var services = BuildServices();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"driverId\":\"{driver:D}\",\"outcome\":\"{wire}\",\"version\":3}}"));
        var reconciler = new DriverCleanupReconciler(
            services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(handler),
            NullLogger<DriverCleanupReconciler>.Instance);

        await reconciler.ReconcileAsync(default);

        var final = await Reload(trip.TripId);
        var release = Assert.Single(final.CleanupIntents, i => i.Kind == TripCleanupKind.ReleaseReservation);
        Assert.Equal(expectCompleted, release.CompletedAt is not null);
        Assert.Null(Assert.Single(final.CleanupIntents, i => i.Kind == TripCleanupKind.CancelMatching).CompletedAt);
        Assert.Equal(TripState.MatchFailed, final.State);
    }

    [Fact]
    public async Task DriverCleanupReconciler_TransportFailureKeepsObligationPending()
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);
        Assert.True((await Reject(trip, driver, reservation)).IsSuccess);

        await using var services = BuildServices();
        var handler = new StubHandler(_ => throw new HttpRequestException("driver unavailable"));
        var reconciler = new DriverCleanupReconciler(
            services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(handler),
            NullLogger<DriverCleanupReconciler>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => reconciler.ReconcileAsync(default));

        var final = await Reload(trip.TripId);
        Assert.Null(Assert.Single(final.CleanupIntents,
            i => i.Kind == TripCleanupKind.ReleaseReservation).CompletedAt);
    }

    [Fact]
    public async Task ConfirmCleanup_IsIdempotentAndRejectsUnknownKey()
    {
        var driver = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var trip = await SeedReserved(driver, reservation);
        Assert.True((await Reject(trip, driver, reservation)).IsSuccess);
        var intent = Assert.Single((await Reload(trip.TripId)).CleanupIntents,
            i => i.Kind == TripCleanupKind.ReleaseReservation);

        await using (var db = Db())
        {
            var confirm = new ConfirmTripCleanupHandler(db, new Clock());
            var first = await confirm.HandleAsync(trip.TripId, intent.Key, CleanupAuthorityResult.Released);
            var replay = await confirm.HandleAsync(trip.TripId, intent.Key, CleanupAuthorityResult.Released);
            var unknown = await confirm.HandleAsync(trip.TripId, "release:" + Guid.NewGuid().ToString("D"),
                CleanupAuthorityResult.Released);
            Assert.True(first.IsSuccess);
            Assert.True(replay.IsSuccess);
            Assert.True(replay.IsReplay);
            Assert.False(unknown.IsSuccess);
        }
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TripDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddScoped<ConfirmTripCleanupHandler>();
        services.AddSingleton<TimeProvider>(new Clock());
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LostCommitResponse : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default) =>
            throw new IOException("Lost commit response (test).");
    }
}
