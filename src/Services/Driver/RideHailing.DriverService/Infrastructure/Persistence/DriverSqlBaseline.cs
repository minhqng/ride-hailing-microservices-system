// B-W03-07: loads ALL W2+ migration scripts in filename order (001, 002, ...).
// Single source shared by the service startup path (chốt ③: Npgsql raw, no EF in W3).
namespace RideHailing.DriverService.Infrastructure.Persistence;

public static class DriverSqlBaseline
{
    public static string Load()
    {
        var dir = FindMigrationsDir();
        var files = Directory.GetFiles(dir, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new FileNotFoundException("No migration *.sql found in " + dir);
        }

        return string.Join("\n", files.Select(File.ReadAllText));
    }

    private static string FindMigrationsDir()
    {
        var candidates = new[]
        {
            // Published service output (csproj copies *.sql to output).
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Persistence", "Migrations"),
            // Dev/test source-tree fallbacks (service + test working dirs).
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations")),
        };
        foreach (var p in candidates)
        {
            if (Directory.Exists(p))
            {
                return p;
            }
        }

        throw new DirectoryNotFoundException(
            "Driver Migrations dir not found. Tried: " + string.Join("; ", candidates));
    }
}
