// Loads ALL matching migration scripts in filename order (001, 002, 003, ...).
// Single source shared by the service startup path (Npgsql raw, no EF).
namespace RideHailing.MatchingService.Infrastructure.Persistence;

public static class MatchingSqlBaseline
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
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Persistence", "Migrations"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "src", "Services", "Matching", "RideHailing.MatchingService",
                "Infrastructure", "Persistence", "Migrations")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "src", "Services", "Matching", "RideHailing.MatchingService",
                "Infrastructure", "Persistence", "Migrations")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "src", "Services", "Matching", "RideHailing.MatchingService",
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
            "Matching Migrations dir not found. Tried: " + string.Join("; ", candidates));
    }
}
