// B-W04-04: loads the single-source Lua function (dev source tree or published output).
namespace RideHailing.MatchingService.Application.Projection;

public static class RedisFunctionSource
{
    public const string FileName = "update_projection.lua";

    public static string Load()
    {
        foreach (var p in CandidatePaths())
        {
            if (File.Exists(p))
            {
                return File.ReadAllText(p);
            }
        }

        throw new FileNotFoundException(
            FileName + " not found. Tried: " + string.Join("; ", CandidatePaths()));
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory,
            "Infrastructure", "RedisProjection", FileName);
        yield return Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "Services", "Matching", "RideHailing.MatchingService",
            "Infrastructure", "RedisProjection", FileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(),
            "src", "Services", "Matching", "RideHailing.MatchingService",
            "Infrastructure", "RedisProjection", FileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(),
            "..", "..", "..", "..",
            "src", "Services", "Matching", "RideHailing.MatchingService",
            "Infrastructure", "RedisProjection", FileName);
    }
}
