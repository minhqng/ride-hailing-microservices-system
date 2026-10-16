// B-W02-02: total-mapping proof for the Driver seam (stub transport, no server).
// Every row: request shape asserted + outcome mapping asserted.
using System.Net;
using System.Text;
using System.Text.Json;
using RideHailing.MatchingService.Infrastructure.DriverClient;

namespace RideHailing.MatchingService.UnitTests;

public class DriverClientMappingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return Responder(request);
        }
    }

    private static HttpDriverClient Client(
        StubHandler stub, out StubHandler captured, TimeSpan? timeout = null)
    {
        captured = stub;
        var http = new HttpClient(stub)
        {
            BaseAddress = new Uri("http://driver:8080/"),
        };
        if (timeout.HasValue)
        {
            http.Timeout = timeout.Value;
        }

        return new HttpDriverClient(http);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task Reserve_Sends_Shape_And_Maps_Reserved()
    {
        var driver = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var res = Guid.NewGuid();
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.OK, new { status = "RESERVED", version = 7 }),
        };
        var client = Client(stub, out var captured);

        var result = await client.ReserveAsync(new(driver, trip, res, 3));

        Assert.Equal(HttpMethod.Post, captured.LastRequest!.Method);
        Assert.Equal($"/drivers/{driver:N}/reserve", captured.LastRequest.RequestUri!.AbsolutePath);
        var body = JsonDocument.Parse(captured.LastBody!).RootElement;
        Assert.Equal(trip.ToString(), body.GetProperty("tripId").GetString());
        Assert.Equal(res.ToString(), body.GetProperty("reservationId").GetString());
        Assert.Equal(3, body.GetProperty("expectedVersion").GetInt32());
        Assert.Equal(DriverReserveOutcomeKind.Reserved, result.Kind);
        Assert.Equal(7, result.DriverVersion);
    }

    [Fact]
    public async Task Reserve_409_Preserves_Code()
    {
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.Conflict, new { code = "DRIVER_NOT_AVAILABLE" }),
        };
        var client = Client(stub, out _);

        var result = await client.ReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));

        Assert.Equal(DriverReserveOutcomeKind.Rejected, result.Kind);
        Assert.Equal("DRIVER_NOT_AVAILABLE", result.Code);
    }

    [Fact]
    public async Task Reserve_409_Preserves_NonDefault_Code()
    {
        // A code other than the fallback proves real parsing (not the default kicking in).
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.Conflict, new { code = "RATE_LIMITED_TRY_LATER" }),
        };
        var client = Client(stub, out _);

        var result = await client.ReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));

        Assert.Equal(DriverReserveOutcomeKind.Rejected, result.Kind);
        Assert.Equal("RATE_LIMITED_TRY_LATER", result.Code);
    }

    [Fact]
    public async Task Reserve_404_Maps_NotFound()
    {
        var stub = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = Client(stub, out _);

        var result = await client.ReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));

        Assert.Equal(DriverReserveOutcomeKind.Rejected, result.Kind);
        Assert.Equal("DRIVER_NOT_FOUND", result.Code);
    }

    [Fact]
    public async Task Reserve_500_Throws_Transport()
    {
        var stub = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var client = Client(stub, out _);

        // Never a business answer: caller classifies UNKNOWN (W6/W7).
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1)));
    }

    [Fact]
    public async Task Reserve_Timeout_Propagates_Not_Mapped()
    {
        var stub = new StubHandler
        {
            Responder = _ => throw new TaskCanceledException("simulated timeout"),
        };
        var client = Client(stub, out _);

        // Timeout ≠ failure: must propagate so the caller reconciles same id.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.ReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1)));
    }

    [Theory]
    [InlineData("OWNED_BY_THIS_RESERVATION", ReservationReconcileOutcome.OwnedByThisReservation)]
    [InlineData("NOT_COMMITTED_OR_AVAILABLE", ReservationReconcileOutcome.NotCommittedOrAvailable)]
    [InlineData("RESERVED_BY_OTHER", ReservationReconcileOutcome.ReservedByOther)]
    [InlineData("OFFLINE", ReservationReconcileOutcome.Offline)]
    [InlineData("ON_TRIP", ReservationReconcileOutcome.OnTrip)]
    [InlineData("NOT_FOUND", ReservationReconcileOutcome.NotFound)]
    public async Task Reconcile_Maps_All_Canonical_Outcomes(string wire, ReservationReconcileOutcome expected)
    {
        var driver = Guid.NewGuid();
        var res = Guid.NewGuid();
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.OK, new { outcome = wire, version = 7 }),
        };
        var client = Client(stub, out var captured);

        var result = await client.GetReservationAsync(driver, res);

        Assert.Equal(HttpMethod.Get, captured.LastRequest!.Method);
        Assert.Equal(
            $"/internal/drivers/{driver:N}/reservations/{res:N}",
            captured.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(7, result.DriverVersion);
    }

    [Fact]
    public async Task Reconcile_Maps_Version_Null_For_NotFound()
    {
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.OK, new { outcome = "NOT_FOUND", version = (int?)null }),
        };
        var client = Client(stub, out _);

        var result = await client.GetReservationAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(ReservationReconcileOutcome.NotFound, result.Outcome);
        Assert.Null(result.DriverVersion);
    }

    [Fact]
    public async Task Reconcile_Unknown_Outcome_Throws()
    {
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.OK, new { outcome = "SOMETHING_NEW" }),
        };
        var client = Client(stub, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetReservationAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Reconcile_Bare_Http404_Throws_Never_Maps_NotFound()
    {
        // DSI-031: a bare 404 carries no domain body — it may be a wrong
        // BaseAddress/proxy/missing route. Mapping it to conclusive NOT_FOUND
        // would let the caller advance to the next candidate while an earlier
        // reservation may already be committed (double reservation).
        // NOT_FOUND arrives ONLY as a 200 body outcome (proven above).
        var driver = Guid.NewGuid();
        var res = Guid.NewGuid();
        var stub = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = Client(stub, out var captured);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetReservationAsync(driver, res));

        Assert.Equal(HttpMethod.Get, captured.LastRequest!.Method);
        Assert.Equal(
            $"/internal/drivers/{driver:N}/reservations/{res:N}",
            captured.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Reconcile_Http500_Still_Throws_Transport()
    {
        var stub = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var client = Client(stub, out _);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetReservationAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task VersionRead_Maps_Version_And_Status()
    {
        var driver = Guid.NewGuid();
        var stub = new StubHandler
        {
            Responder = _ => JsonResponse(HttpStatusCode.OK, new { version = 9, status = "AVAILABLE" }),
        };
        var client = Client(stub, out var captured);

        var read = await client.TryGetDriverVersionAsync(driver);

        Assert.Equal(HttpMethod.Get, captured.LastRequest!.Method);
        Assert.Equal($"/drivers/{driver:N}", captured.LastRequest.RequestUri!.AbsolutePath);
        Assert.NotNull(read);
        Assert.Equal(9, read!.Version);
        Assert.Equal("AVAILABLE", read.Status);
    }

    [Fact]
    public async Task VersionRead_404_Maps_Null()
    {
        var stub = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = Client(stub, out _);

        Assert.Null(await client.TryGetDriverVersionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task VersionRead_500_Throws_Transport()
    {
        var stub = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        var client = Client(stub, out _);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.TryGetDriverVersionAsync(Guid.NewGuid()));
    }
}
