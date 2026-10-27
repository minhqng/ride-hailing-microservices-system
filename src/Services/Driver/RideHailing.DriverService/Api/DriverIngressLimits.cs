// B-W12-06/SEC-07: Driver accepts only small JSON command bodies. The limit
// is applied by Kestrel before endpoint binding so an oversized body is
// rejected without reaching the location validator or any mutation.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace RideHailing.DriverService.Api;

public static class DriverIngressLimits
{
    public const long MaxRequestBodyBytes = 64 * 1024;

    public static void Apply(WebApplicationBuilder builder) =>
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
}
