// B-W06-01: frozen HTTP contract for POST /drivers/{id}/reserve, proven by
// executing the pure mapper (no HTTP server, no new test packages).
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RideHailing.DriverService.Api;
using RideHailing.DriverService.Domain.Drivers;
using RideHailing.DriverService.Infrastructure.Persistence;

namespace RideHailing.DriverService.UnitTests;

public class DriverReserveResultsTests
{
    private static Driver ReservedDriver(out Guid trip, out Guid reservation)
    {
        var d = Driver.Create(Guid.NewGuid(), Guid.NewGuid());
        d.GoOnline();
        trip = Guid.NewGuid();
        reservation = Guid.NewGuid();
        d.Reserve(trip, reservation, d.Version);
        return d;
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
    public async Task Won_Maps_To_200_With_Canonical_Body()
    {
        var d = ReservedDriver(out var t, out var r);
        var (status, body) = await ExecuteAsync(
            DriverReserveResults.ToHttpResult(ReserveResult.Won(d)));
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"outcome\":\"won\"", body);
        Assert.Contains(t.ToString(), body);
        Assert.Contains(r.ToString(), body);
    }

    [Fact]
    public async Task Replayed_Maps_To_200_With_Replayed_Outcome()
    {
        var d = ReservedDriver(out _, out _);
        var (status, body) = await ExecuteAsync(
            DriverReserveResults.ToHttpResult(ReserveResult.Replayed(d)));
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"outcome\":\"replayed\"", body);
    }

    [Theory]
    [InlineData(DriverErrors.DriverNotAvailable)]
    [InlineData(DriverErrors.StaleVersion)]
    [InlineData(DriverErrors.StaleReservation)]
    public async Task Conflict_Maps_To_409_With_Stable_Code(string code)
    {
        var (status, body) = await ExecuteAsync(
            DriverReserveResults.ToHttpResult(ReserveResult.Conflict(code, "domain reason")));
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Contains($"\"code\":\"{code}\"", body);
    }

    [Fact]
    public async Task NotFound_Maps_To_404()
    {
        var (status, _) = await ExecuteAsync(
            DriverReserveResults.ToHttpResult(ReserveResult.NotFound()));
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }
}
