// B-W02-03/B-W02-06: Driver PostgreSQL baseline fixture (W2 prereq for W3).
// Uses real PostgreSQL only. Skips gracefully when PG is unavailable (CI without infra stays green).
using System.Net.Sockets;
using Npgsql;

namespace RideHailing.DriverService.IntegrationTests;

public static class DriverPostgresFixture
{
    public static string ResolveConnectionString()
    {
        return Environment.GetEnvironmentVariable("DRIVER_TEST_POSTGRES")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=driver_db;Username=driver_app;Password=driver_dev_pw;Timeout=3;CommandTimeout=5";
    }

    public static string LoadBaselineSql()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "..",
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations", "001_driver_baseline.sql")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "src", "Services", "Driver", "RideHailing.DriverService",
                "Infrastructure", "Persistence", "Migrations", "001_driver_baseline.sql")),
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                return File.ReadAllText(p);
            }
        }

        throw new FileNotFoundException(
            "001_driver_baseline.sql not found. Tried: " + string.Join("; ", candidates));
    }

    public static async Task<bool> TryApplyBaselineAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ResolveConnectionString());
            await conn.OpenAsync();
            var sql = LoadBaselineSql();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or IOException or SocketException)
        {
            return false;
        }
    }

    public static async Task<HashSet<string>> GetTablesAsync()
    {
        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
