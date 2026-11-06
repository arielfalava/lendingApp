using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Infrastructure;

public static class DatabaseSetup
{
    /// <summary>
    /// Creates the schema when it's missing. Plain EnsureCreated isn't enough: it skips any SQLite file that
    /// still contains a table, including SQLite's internal <c>sqlite_sequence</c>, which survives after the
    /// app's tables are dropped and cannot itself be dropped.
    /// </summary>
    public static async Task EnsureSchemaAsync(LendingDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        var appTables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
            .ToListAsync();
        if (appTables.Count == 0)
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
    }
}
