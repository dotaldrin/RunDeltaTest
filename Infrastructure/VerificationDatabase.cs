namespace ApplicationTesting.Infrastructure;

/// <summary>
/// One disposable LocalDB database per test run. Tests isolate themselves by
/// seeding distinct Tenants, which also exercises the SUT's tenant scoping.
/// </summary>
[TestClass]
public static class VerificationDatabase
{
    private static LocalDbDatabase? _database;

    public static LocalDbDatabase Instance =>
        _database ?? throw new InvalidOperationException(
            "Verification database has not been initialized.");

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext _)
    {
        _database = await LocalDbDatabase.CreateAsync("Billing");
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
