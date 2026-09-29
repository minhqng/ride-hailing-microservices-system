// Shared disposable real-PostgreSQL fixture (A-W02-05).
//
// Uses Testcontainers.PostgreSql with the repository-pinned PostgreSQL family
// (postgres:18.6, same as compose.yaml). One container per test run, started
// once and shared via xUnit collection fixture; xUnit disposes it afterwards.
// No fixed host port, no persistent database pollution, no sleep-based
// readiness (Testcontainers waits for actual PostgreSQL readiness).
//
// CREDENTIAL SCOPE: the container bootstrap credential below is an
// ephemeral Testcontainers-generated value for disposable component setup. It
// MUST NOT be reported as least-privilege (trip_app) or cross-database
// isolation proof. Those claims belong to the C-owned role/database bootstrap
// (deploy/postgres/init) and are proven separately, if executable.

using Testcontainers.PostgreSql;

namespace RideHailing.TripService.IntegrationTests;

/// <summary>
/// xUnit collection binding for the shared disposable PostgreSQL container.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TripPostgresCollection : ICollectionFixture<TripPostgresFixture>
{
    public const string Name = "Trip real-PostgreSQL (Testcontainers)";
}

/// <summary>
/// Starts one disposable real PostgreSQL server for the Trip component suite.
/// Database/user names mirror the C-owned convention (trip_db/trip_app) so the
/// component path matches production naming; the credential itself is an
/// ephemeral container value, NOT the C-owned role proof.
/// </summary>
public sealed class TripPostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:18.6")
        .WithDatabase("trip_db")
        .WithUsername("trip_app")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync() => await Container.StartAsync();

    public async Task DisposeAsync() => await Container.DisposeAsync();
}
