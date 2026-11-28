using System.Globalization;
using Haruspex.DifferentialTests.Validation;
using Microsoft.Data.SqlClient;

namespace Haruspex.DifferentialTests.Server;

/// <summary>
/// A database of one test's own in which the CLR assembly is created (<see cref="ClrAssembly"/>), trusted by its hash for as
/// long as the database stands: disposing it withdraws the trust and drops the database. <c>clr strict security</c> stays on,
/// so nothing else is trusted. A server started for the run has <c>clr enabled</c> turned on; on one the developer gave,
/// where it is off, the tests are skipped saying so, as a setting of their server is theirs to change.
/// </summary>
internal sealed class ClrDatabase : IAsyncDisposable
{
    private const string Description = "haruspex_test";

    private readonly ScratchDatabase database;
    private readonly ScratchSession session;

    private ClrDatabase(ScratchDatabase database, ScratchSession session)
    {
        this.database = database;
        this.session = session;
    }

    /// <summary>The database, with the assembly created in it.</summary>
    /// <param name="server">The server.</param>
    /// <param name="collation">The database's collation; null for the server's.</param>
    public static async Task<ClrDatabase> CreateAsync(SqlServerFixture server, string? collation = null)
    {
        var token = TestContext.Current.CancellationToken;
#pragma warning disable CA2000 // The database and its session are the returned ClrDatabase's, or disposed below.
        var database = await server.ScratchAsync(collation);
        ClrDatabase? clr = null;
        try
        {
            clr = new ClrDatabase(database, await database.SessionAsync(token));
#pragma warning restore CA2000
            await clr.EnableAsync(ownServer: server.Given is null);
            var assembly = ClrAssembly.Built;

            // Every test of a run trusts the same hash, and withdraws it when it ends, so one at a time holds it: by a lock named
            // for the hash, taken in master, which every test's session shares, until this session closes.
            await clr.ExecuteAsync($"EXEC master.sys.sp_getapplock @Resource = N'haruspex_test_{assembly.Hash[..34]}', @LockMode = 'Exclusive', @LockOwner = 'Session';");

            // What a killed run left trusted is withdrawn after a day, as its databases are dropped.
            await clr.ExecuteAsync($"""
                DECLARE @left varbinary(64);
                WHILE 1 = 1
                BEGIN
                    SET @left = (SELECT TOP (1) hash FROM sys.trusted_assemblies WHERE description = N'{Description}' AND create_date < DATEADD(day, -1, SYSUTCDATETIME()));
                    IF @left IS NULL BREAK;
                    EXEC sys.sp_drop_trusted_assembly @left;
                END;
                """);
            await clr.ExecuteAsync($"IF NOT EXISTS (SELECT 1 FROM sys.trusted_assemblies WHERE hash = {assembly.Hash}) EXEC sys.sp_add_trusted_assembly {assembly.Hash}, N'{Description}';");
            await clr.ExecuteAsync($"CREATE ASSEMBLY Shelf FROM {assembly.Literal} WITH PERMISSION_SET = SAFE;");
            return clr;
        }
        catch
        {
            // With the session, the trust too, if it was given before the failure.
            if (clr is not null)
                await clr.DisposeAsync();
            else
                await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a batch.</summary>
    public async Task ExecuteAsync(string batch)
    {
        await using var command = session.Command(batch);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Every result set a batch returns, as a client reads them.</summary>
    public async Task<CallOutcome> CallAsync(string batch)
    {
        await using var command = session.Command(batch);
        return await ObservedCall.Read(command, errorsAsMessages: false, TestContext.Current.CancellationToken);
    }

    /// <summary>The number of the error a batch fails with; null when it succeeds.</summary>
    public async Task<int?> ErrorAsync(string batch)
    {
        try
        {
            await ExecuteAsync(batch);
            return null;
        }
        catch (SqlException e)
        {
            return e.Number;
        }
    }

    /// <summary>Runs a procedure of the database, with its result sets.</summary>
    public async Task<IReadOnlyList<ObservedResultSet>> RunAsync(string procedure)
    {
        await using var ownSession = await database.SessionAsync(TestContext.Current.CancellationToken);
        return await ProcedureRunner.RunAsync(ownSession, new ProcedureRun(procedure, "none", procedure, new Dictionary<string, RunArgument>()));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // The assembly and everything that uses it goes with the database; the trust is the server's, so it is withdrawn.
            await ExecuteAsync($"IF EXISTS (SELECT 1 FROM sys.trusted_assemblies WHERE hash = {ClrAssembly.Built.Hash}) EXEC sys.sp_drop_trusted_assembly {ClrAssembly.Built.Hash};");
        }
        finally
        {
            await session.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private async Task EnableAsync(bool ownServer)
    {
        if (await ScalarAsync("SELECT value_in_use FROM sys.configurations WHERE name = N'clr enabled';") is 1)
            return;
        if (!ownServer)
            Assert.Skip($"CLR is off on the server {SqlServerFixture.ServerVariable} names, so these observations did not run. Run EXEC sp_configure 'clr enabled', 1; RECONFIGURE; on it.");
        await ExecuteAsync("EXEC sp_configure 'clr enabled', 1; RECONFIGURE;");
    }

    private async Task<int> ScalarAsync(string query)
    {
        await using var command = session.Command(query);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }
}
