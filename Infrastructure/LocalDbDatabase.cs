using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RunDelta.Infrastructure.Models;

namespace ApplicationTesting.Infrastructure;

/// <summary>
/// Disposable SQL Server LocalDB database whose schema is generated from the
/// production <see cref="RunDelta_DbContext"/> model (indexes, filtered unique
/// constraints, FKs). Azure SQL is never used.
/// </summary>
public sealed class LocalDbDatabase : IAsyncDisposable
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True";

    public string ConnectionString { get; }

    public string DatabaseName { get; }

    private LocalDbDatabase(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public static async Task<LocalDbDatabase> CreateAsync(string prefix)
    {
        var explicitServer =
            Environment.GetEnvironmentVariable("RUNDELTA_TEST_SQLSERVER");
        var master = string.IsNullOrWhiteSpace(explicitServer)
            ? MasterConnectionString
            : explicitServer;

        if (master.Contains(".windows.net", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Azure SQL is not allowed for verification tests.");
        }

        var name = $"AppTest_{prefix}_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(master)
        {
            InitialCatalog = name
        };

        var database = new LocalDbDatabase(name, builder.ConnectionString);

        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();

        return database;
    }

    public RunDelta_DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RunDelta_DbContext>();
        RunDelta_DbContext.ConfigureSqlServer(options, ConnectionString);
        return new RunDelta_DbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var context = CreateContext();
            await context.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best-effort cleanup; a leaked LocalDB database is not a test failure.
        }
    }
}
