using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace RideHailing.IdentityService.Infrastructure;

public sealed record IdentityAccount(Guid UserId, string Username, string Role, string PasswordHash);

public sealed class IdentityStore(NpgsqlDataSource source)
{
    private readonly PasswordHasher<IdentityAccount> hasher = new();
    private readonly IdentityAccount dummy = new(Guid.Empty, "", "Rider", "");
    private readonly string dummyHash = new PasswordHasher<IdentityAccount>().HashPassword(
        new IdentityAccount(Guid.Empty, "", "Rider", ""), Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS identity_users (
                user_id uuid PRIMARY KEY,
                username text NOT NULL,
                normalized_username text NOT NULL UNIQUE,
                role text NOT NULL CHECK (role IN ('Rider', 'Driver')),
                password_hash text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            """);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IdentityAccount?> RegisterAsync(string username, string password, string role, CancellationToken token)
    {
        var account = new IdentityAccount(Guid.NewGuid(), username.Trim(), role, "");
        var hash = hasher.HashPassword(account, password);
        await using var command = source.CreateCommand("""
            INSERT INTO identity_users (user_id, username, normalized_username, role, password_hash)
            VALUES (@id, @username, @normalized, @role, @hash)
            ON CONFLICT (normalized_username) DO NOTHING
            """);
        command.Parameters.AddWithValue("id", account.UserId);
        command.Parameters.AddWithValue("username", account.Username);
        command.Parameters.AddWithValue("normalized", Normalize(username));
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("hash", hash);
        return await command.ExecuteNonQueryAsync(token) == 1 ? account with { PasswordHash = hash } : null;
    }

    public async Task<IdentityAccount?> AuthenticateAsync(string username, string password, CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            SELECT user_id, username, role, password_hash FROM identity_users WHERE normalized_username=@username
            """);
        command.Parameters.AddWithValue("username", Normalize(username));
        IdentityAccount? account;
        await using (var reader = await command.ExecuteReaderAsync(token))
            account = await reader.ReadAsync(token)
                ? new IdentityAccount(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                : null;
        var result = hasher.VerifyHashedPassword(account ?? dummy, account?.PasswordHash ?? dummyHash, password);
        return account is not null && result != PasswordVerificationResult.Failed ? account : null;
    }

    private static string Normalize(string username) => username.Trim().ToUpperInvariant();
}
