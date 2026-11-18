using RideHailing.Observability;
using RideHailing.Security;
using RideHailing.IdentityService.Infrastructure;
using Npgsql;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRideAuthentication(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddSingleton(NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Identity requires its owned PostgreSQL connection.")));
builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<PrincipalIssuer>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("identity", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.AddRideTelemetry("identity-service");
builder.Services.AddHealthChecks();

var app = builder.Build();
_ = app.Services.GetRequiredService<PrincipalIssuer>();
await app.Services.GetRequiredService<IdentityStore>().InitializeAsync(CancellationToken.None);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapPost("/auth/register", async (RegisterRequest request, IdentityStore store, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Trim().Length is < 3 or > 128 ||
        string.IsNullOrEmpty(request.Password) || request.Password.Length is < 12 or > 1024 ||
        request.Role is not ("Rider" or "Driver"))
        return Results.BadRequest(new { code = "INVALID_REGISTRATION" });
    var account = await store.RegisterAsync(request.Username, request.Password, request.Role, token);
    return account is null ? Results.Conflict(new { code = "ACCOUNT_EXISTS" }) :
        Results.Json(new { account.UserId, account.Role }, statusCode: 201);
}).RequireRateLimiting("identity");

app.MapPost("/auth/login", async (LoginRequest request, IdentityStore store, PrincipalIssuer issuer, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 128 ||
        string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024) return Results.Unauthorized();
    var account = await store.AuthenticateAsync(request.Username, request.Password, token);
    return account is null ? Results.Unauthorized() : Results.Ok(issuer.ForUser(account));
}).RequireRateLimiting("identity");

app.MapPost("/connect/token", (ServiceGrantRequest request, PrincipalIssuer issuer) =>
{
    if (request.ClientId is not ("matching" or "trip") || string.IsNullOrEmpty(request.ClientSecret) ||
        request.ClientSecret.Length > 1024) return Results.Unauthorized();
    var grant = issuer.ForService(request.ClientId, request.ClientSecret);
    return grant is null ? Results.Unauthorized() : Results.Ok(grant);
}).RequireRateLimiting("identity");

app.MapGet("/health/live", () => Results.Ok(new { status = "Live", service = "identity-service" }));
app.MapRideReadiness(requiresOwnedDatabase: true);
app.MapGet("/info", (IHostEnvironment env) =>
    Results.Ok(new { serviceName = "identity-service", environment = env.EnvironmentName }));

app.Run();

internal sealed record RegisterRequest(string Username, string Password, string Role);
internal sealed record LoginRequest(string Username, string Password);
internal sealed record ServiceGrantRequest(string ClientId, string ClientSecret);
