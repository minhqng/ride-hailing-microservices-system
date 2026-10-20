using System.Reflection;
using Npgsql;

namespace RideHailing.Messaging.Schema;

/// <summary>Applies an idempotent embedded SQL resource to the service-owned data source.</summary>
internal static class EmbeddedSql
{
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, string fileName, CancellationToken token)
    {
        var assembly = typeof(EmbeddedSql).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded SQL resource '{fileName}' is missing.");
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(token);
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
