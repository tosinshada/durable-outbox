using DurableOutbox.Tests;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace DurableOutbox.Tests;

/// <summary>One PostgreSQL container for the whole run. Each test gets its own database in it.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int _databases;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"test_{Interlocked.Increment(ref _databases)}";

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// Refuses or allows new connections to one test's database. Refusing also ends its open connections,
    /// so the database looks unreachable to that test only.
    /// </summary>
    public async Task SetConnectionsAllowedAsync(string connectionString, bool allowed, CancellationToken cancellationToken)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var alter = new NpgsqlCommand(
            $"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS {(allowed ? "true" : "false")}", connection))
        {
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!allowed)
        {
            await using var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name", connection);
            terminate.Parameters.AddWithValue("name", name);
            await terminate.ExecuteNonQueryAsync(cancellationToken);
        }

        // Idle pooled connections to that database are dead either way.
        await using var pooled = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(pooled);
    }
}
