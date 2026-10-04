// TEST-ONLY authenticated-principal injector for CreateRide HTTP tests.
//
// Production TripService registers NO authentication middleware and NO test
// headers: anonymous production requests safely receive 401 until shared
// Identity/Gateway middleware populates HttpContext.User. This
// IStartupFilter exists ONLY in the test host (registered via
// ConfigureTestServices) and builds a ClaimsPrincipal per request from
// X-Test-* headers, which production never reads.

using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace RideHailing.TripService.IntegrationTests;

/// <summary>
/// Test-host-only middleware source that materializes a principal from
/// per-request test headers.
/// </summary>
public sealed class TestPrincipalStartupFilter : IStartupFilter
{
    public const string NameIdentifierHeader = "X-Test-NameId";
    public const string SubjectHeader = "X-Test-Sub";
    public const string RoleHeader = "X-Test-Role";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, invoke) =>
        {
            var headers = context.Request.Headers;
            var hasAny = headers.ContainsKey(NameIdentifierHeader)
                || headers.ContainsKey(SubjectHeader)
                || headers.ContainsKey(RoleHeader);

            if (hasAny)
            {
                var claims = new List<Claim>();
                if (headers.TryGetValue(NameIdentifierHeader, out var nameIds))
                {
                    foreach (var value in nameIds)
                    {
                        claims.Add(new Claim(ClaimTypes.NameIdentifier, value ?? string.Empty));
                    }
                }

                if (headers.TryGetValue(SubjectHeader, out var subs))
                {
                    foreach (var value in subs)
                    {
                        claims.Add(new Claim("sub", value ?? string.Empty));
                    }
                }

                if (headers.TryGetValue(RoleHeader, out var roles))
                {
                    foreach (var value in roles)
                    {
                        claims.Add(new Claim(ClaimTypes.Role, value ?? string.Empty));
                    }
                }

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await invoke();
        });
        next(app);
    };
}
