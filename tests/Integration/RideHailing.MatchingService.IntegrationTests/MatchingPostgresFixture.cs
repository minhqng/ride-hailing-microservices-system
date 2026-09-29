// B-W02-04: Matching PostgreSQL baseline fixture. Real PG only, skip when unavailable.
using System.Net.Sockets;
using Npgsql;

namespace RideHailing.MatchingService.IntegrationTests;

public static class MatchingPostgresFixture
{
    public static string ResolveConnectionString()
    {
        return Environment.GetEnvironmentVariable("MATCHING_TEST_POSTGRES")
            ?? "Host=localhost;Port=5432;Database=matching_db;Username=matching_app;Password=matching_dev_pw;Timeout=3;CommandTimeout=5";
    }

    public static string LoadBaselineSql()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "src", "Services", "Matching", "RideHailing.MatchingService",
                "Infrastructure", "Persistence", "Migrations", "001_matching_baseline.sql")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "src", "Services", "Matching", "RideHailing.MatchingService",
                "Infrastructure", "Persistence", "Migrations", "001_matching_baseline.sql")),
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                return File.ReadAllText(p);
            }
        }

        throw new FileNotFoundException(
            "001_matching_baseline.sql not found. Tried: " + string.Join("; ", candidates));
    }

    public static async Task<bool> TryApplyBaselineAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ResolveConnectionString());
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(LoadBaselineSql(), conn);
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
