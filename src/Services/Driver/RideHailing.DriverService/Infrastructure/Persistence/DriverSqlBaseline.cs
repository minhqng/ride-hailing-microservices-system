// B-W03-07: loads the single-source W2 SQL baseline (chốt ③: Npgsql raw, no EF in W3).
namespace RideHailing.DriverService.Infrastructure.Persistence;

public static class DriverSqlBaseline
{
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
            "001_driver_baseline.sql not found. Tried: " + string.Join("; ", CandidatePaths()));
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // Published service output (csproj copies *.sql to output).
        yield return Path.Combine(AppContext.BaseDirectory,
            "Infrastructure", "Persistence", "Migrations", "001_driver_baseline.sql");
        // Dev/test source-tree fallbacks (service + test working dirs).
        yield return Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "src", "Services", "Driver", "RideHailing.DriverService",
            "Infrastructure", "Persistence", "Migrations", "001_driver_baseline.sql");
        yield return Path.Combine(Directory.GetCurrentDirectory(),
            "src", "Services", "Driver", "RideHailing.DriverService",
            "Infrastructure", "Persistence", "Migrations", "001_driver_baseline.sql");
        yield return Path.Combine(Directory.GetCurrentDirectory(),
            "..", "..", "..", "..",
            "src", "Services", "Driver", "RideHailing.DriverService",
            "Infrastructure", "Persistence", "Migrations", "001_driver_baseline.sql");
    }
}
