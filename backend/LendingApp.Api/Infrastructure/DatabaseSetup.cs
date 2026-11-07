using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Infrastructure;

public static class DatabaseSetup
{
    /// <summary>
    /// Creates the schema when it's missing, then applies in-place upgrades so existing data survives model changes.
    /// </summary>
    public static async Task EnsureSchemaAsync(LendingDbContext db)
    {
        // Plain EnsureCreated isn't enough: it skips any SQLite file that still contains a table, including
        // SQLite's internal sqlite_sequence, which survives after the app's tables are dropped.
        await db.Database.EnsureCreatedAsync();
        var appTables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
            .ToListAsync();
        if (appTables.Count == 0)
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            return;
        }

        await UpgradeAsync(db);
    }

    /// <summary>
    /// Schema changes made after databases already held real data. Each step checks before acting, so it's safe
    /// to run on every start. (A fresh database is created with the current schema and skips these.)
    /// </summary>
    private static async Task UpgradeAsync(LendingDbContext db)
    {
        var loanColumns = await Columns(db, "Loans");

        // 2026-11: loan term can be weeks or months. "TermMonths" becomes "Term" + "TermUnit".
        if (loanColumns.Contains("TermMonths") && !loanColumns.Contains("Term"))
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Loans" RENAME COLUMN "TermMonths" TO "Term";""");
        if (!loanColumns.Contains("TermUnit"))
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Loans" ADD COLUMN "TermUnit" TEXT NOT NULL DEFAULT 'Months';""");

        // 2026-11: sign-in. Same shape EF would create for AppUser.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Users" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
                "Email" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email" ON "Users" ("Email");
            """);
    }

    private static async Task<HashSet<string>> Columns(LendingDbContext db, string table) =>
        (await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{table}')").ToListAsync())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
