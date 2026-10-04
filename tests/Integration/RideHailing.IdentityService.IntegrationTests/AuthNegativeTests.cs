using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace RideHailing.IdentityService.IntegrationTests;

public sealed class AuthHandoffTheoryAttribute : TheoryAttribute
{
    public AuthHandoffTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RIDE_AUTH_FIXTURE")))
            Skip = "Blocked: A/B must supply real auth/API fixtures via RIDE_AUTH_FIXTURE; skeleton is not auth acceptance.";
    }
}

public class AuthNegativeTests
{
    private sealed record Case(string Method, string Path, string? Body, string? TokenEnvironment);
    private sealed record Fixture(string BaseUrl, string ControlPath, string ControlTokenEnvironment,
        string DatabaseEnvironment, string SnapshotSql, Dictionary<string, Case> Cases);

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing fixture environment variable: {name}");

    [AuthHandoffTheory]
    [InlineData("missing-token", 401)]
    [InlineData("invalid-signature", 401)]
    [InlineData("expired-token", 401)]
    [InlineData("malformed-token", 401)]
    [InlineData("wrong-role", 403)]
    [InlineData("foreign-object", 403)]
    [InlineData("spoofed-actor", 403)]
    public async Task RejectedRequestsDoNotMutateAuthoritativeState(string name, int expectedStatus)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(
            await File.ReadAllTextAsync(RequiredEnvironment("RIDE_AUTH_FIXTURE")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Empty auth fixture");
        var testCase = fixture.Cases[name];
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(fixture.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        using var control = new HttpRequestMessage(HttpMethod.Get, fixture.ControlPath);
        control.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequiredEnvironment(fixture.ControlTokenEnvironment));
        using var controlResponse = await client.SendAsync(control);
        Assert.Equal(200, (int)controlResponse.StatusCode);

        async Task<string> Snapshot()
        {
            await using var connection = new NpgsqlConnection(RequiredEnvironment(fixture.DatabaseEnvironment));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await readOnly.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(fixture.SnapshotSql, connection, transaction) { CommandTimeout = 5 };
            var value = (await command.ExecuteScalarAsync())?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(value), "Snapshot must contain authoritative state, not NULL");
            return value!;
        }
        var before = await Snapshot();
        using var request = new HttpRequestMessage(new HttpMethod(testCase.Method), testCase.Path);
        if (name == "missing-token") Assert.Null(testCase.TokenEnvironment);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequiredEnvironment(
            testCase.TokenEnvironment ?? throw new InvalidOperationException("Token fixture required")));
        if (testCase.Body is not null) request.Content = new StringContent(testCase.Body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(before, await Snapshot());
    }
}
