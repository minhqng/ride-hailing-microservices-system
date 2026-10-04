// B-W04-01: key layout unit proof (no Redis).
using RideHailing.MatchingService.Application.Projection;

namespace RideHailing.MatchingService.UnitTests;

public class ProjectionKeyTests
{
    [Fact]
    public void Keys_Are_Deterministic_And_Scoped()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("driver:projection:11111111222233334444555555555555",
            RedisProjectionKeys.ProjectionKey(id));
        Assert.Equal("drivers:geo", RedisProjectionKeys.GeoKey);
        Assert.Equal("11111111222233334444555555555555", RedisProjectionKeys.Member(id));
    }
}
