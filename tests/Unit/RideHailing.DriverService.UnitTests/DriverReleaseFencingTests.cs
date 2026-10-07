// B-W07-01/02/04: exact release, stale-release fencing, and version-fenced
// online/offline (DSI-004/033, DSR-004 Race B, DSI-003). Pure unit, no PG.
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.UnitTests;

public class DriverReleaseFencingTests
{
    private static Driver AvailableDriver()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline(d.Version);
        return d;
    }

    private static Driver ReservedDriver(out Guid trip, out Guid reservation)
    {
        var d = AvailableDriver();
        trip = Guid.NewGuid();
        reservation = Guid.NewGuid();
        d.Reserve(trip, reservation, d.Version);
        return d;
    }

    [Fact]
    public void Release_Exact_Token_Frees_With_Version_Bump()
    {
        var d = ReservedDriver(out var t, out var r);
        var v = d.Version;
        d.Release(t, r);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v + 1, d.Version);
        Assert.Null(d.ReservedTripId);
        Assert.Null(d.ReservationId);
    }

    [Fact]
    public void Release_Stale_Token_On_Live_Reservation_Conflicts_Without_Mutation()
    {
        var d = ReservedDriver(out _, out var r);
        var v = d.Version;
        var ex = Assert.Throws<DriverDomainException>(() => d.Release(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(DriverErrors.StaleReservation, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(r, d.ReservationId);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void Release_Duplicate_After_Success_Is_Idempotent_Noop()
    {
        var d = ReservedDriver(out var t, out var r);
        d.Release(t, r);
        var v = d.Version;
        d.Release(t, r);
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void Release_On_Never_Held_Available_Is_Idempotent_Noop()
    {
        var d = AvailableDriver();
        var v = d.Version;
        d.Release(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(DriverStatus.Available, d.Status);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void Release_While_OnTrip_Rejected()
    {
        var d = ReservedDriver(out var t, out var r);
        d.MarkTripStarted(t, r);
        Assert.Throws<DriverDomainException>(() => d.Release(t, r));
        Assert.Equal(DriverStatus.OnTrip, d.Status);
    }

    [Fact]
    public void Release_Empty_Identity_Rejected()
    {
        var d = ReservedDriver(out _, out _);
        Assert.Throws<DriverDomainException>(() => d.Release(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<DriverDomainException>(() => d.Release(Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void GoOffline_Stale_Version_Fenced_Even_When_Available()
    {
        var d = AvailableDriver();
        var stale = d.Version;
        d.AttachVehicle(Guid.NewGuid());
        var ex = Assert.Throws<DriverDomainException>(() => d.GoOffline(stale));
        Assert.Equal(DriverErrors.StaleVersion, ex.Code);
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Fact]
    public void GoOffline_Replay_When_Already_Offline_Ignores_Stale_Version()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        var stale = d.Version;
        d.GoOffline(999);
        Assert.Equal(DriverStatus.Offline, d.Status);
        Assert.Equal(stale, d.Version);
    }

    [Fact]
    public void GoOffline_On_Reserved_Rejects_Without_Mutation()
    {
        var d = ReservedDriver(out _, out var r);
        var v = d.Version;
        var ex = Assert.Throws<DriverDomainException>(() => d.GoOffline(v));
        Assert.Equal(DriverErrors.ReservationActive, ex.Code);
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(r, d.ReservationId);
        Assert.Equal(v, d.Version);
    }

    [Fact]
    public void GoOnline_Stale_Version_Fenced()
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        var stale = d.Version;
        d.AttachVehicle(Guid.NewGuid());
        var ex = Assert.Throws<DriverDomainException>(() => d.GoOnline(stale));
        Assert.Equal(DriverErrors.StaleVersion, ex.Code);
        Assert.Equal(DriverStatus.Offline, d.Status);
    }

    [Fact]
    public void GoOnline_Replay_When_Available_Ignores_Stale_Version()
    {
        var d = AvailableDriver();
        var stale = d.Version;
        d.AttachVehicle(Guid.NewGuid());
        d.GoOnline(stale);
        Assert.Equal(DriverStatus.Available, d.Status);
    }

    [Fact]
    public void GoOnline_On_Reserved_Rejects_Without_Mutation()
    {
        var d = ReservedDriver(out _, out _);
        var v = d.Version;
        Assert.Throws<DriverDomainException>(() => d.GoOnline(v));
        Assert.Equal(DriverStatus.Reserved, d.Status);
        Assert.Equal(v, d.Version);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        ctx.RequestServices = services.BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return (ctx.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Mutation_Applied_Maps_To_200()
    {
        var d = ReservedDriver(out _, out _);
        var (status, body) = await ExecuteAsync(
            DriverStateResults.ToHttpResult(DriverMutationResult.Applied(d), "released"));
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"outcome\":\"released\"", body);
    }

    [Fact]
    public async Task Mutation_Unchanged_Maps_To_200_Noop()
    {
        var d = AvailableDriver();
        var (status, body) = await ExecuteAsync(
            DriverStateResults.ToHttpResult(DriverMutationResult.Unchanged(d), "released"));
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"outcome\":\"noop\"", body);
    }

    [Fact]
    public async Task Mutation_Conflict_Maps_To_409_With_Stable_Code()
    {
        var (status, body) = await ExecuteAsync(
            DriverStateResults.ToHttpResult(
                DriverMutationResult.Conflict(DriverErrors.StaleReservation, "stale"), "released"));
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Contains($"\"code\":\"{DriverErrors.StaleReservation}\"", body);
    }

    [Fact]
    public async Task Mutation_NotFound_Maps_To_404()
    {
        var (status, _) = await ExecuteAsync(
            DriverStateResults.ToHttpResult(DriverMutationResult.NotFound(), "released"));
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Theory]
    [InlineData(ReservationQueryOutcome.OwnedByThisReservation, "OWNED_BY_THIS_RESERVATION")]
    [InlineData(ReservationQueryOutcome.NotCommittedOrAvailable, "NOT_COMMITTED_OR_AVAILABLE")]
    [InlineData(ReservationQueryOutcome.ReservedByOther, "RESERVED_BY_OTHER")]
    [InlineData(ReservationQueryOutcome.Offline, "OFFLINE")]
    [InlineData(ReservationQueryOutcome.OnTrip, "ON_TRIP")]
    [InlineData(ReservationQueryOutcome.NotFound, "NOT_FOUND")]
    public async Task Reconcile_Query_Maps_To_200_Canonical_Outcome(
        ReservationQueryOutcome outcome, string wire)
    {
        var (status, body) = await ExecuteAsync(
            DriverStateResults.ToHttpResult(new ReservationQuery(outcome, 7), Guid.NewGuid()));
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains($"\"outcome\":\"{wire}\"", body);
    }
}
