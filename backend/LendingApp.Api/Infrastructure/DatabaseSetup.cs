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

        // 2026-11: Sign in with Google.
        if (!(await Columns(db, "Users")).Contains("GoogleSubject"))
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Users" ADD COLUMN "GoogleSubject" TEXT NULL;""");
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_GoogleSubject" ON "Users" ("GoogleSubject");""");

        // 2026-11: each user has their own data. Rows from before this get an owner in AssignUnownedDataAsync.
        foreach (var table in OwnedTables)
        {
            if (!(await Columns(db, table)).Contains("OwnerId"))
                await db.Database.ExecuteSqlRawAsync($"""ALTER TABLE "{table}" ADD COLUMN "OwnerId" TEXT NULL;""");
            if (table != "Borrowers")
                await db.Database.ExecuteSqlRawAsync($"""CREATE INDEX IF NOT EXISTS "IX_{table}_OwnerId" ON "{table}" ("OwnerId");""");
        }
        // Borrower emails become unique per user instead of across everyone.
        await db.Database.ExecuteSqlRawAsync("""
            DROP INDEX IF EXISTS "IX_Borrowers_Email";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Borrowers_OwnerId_Email" ON "Borrowers" ("OwnerId", "Email");
            """);
    }

    private static readonly string[] OwnedTables = ["Borrowers", "Loans", "Payments", "AuditEntries"];

    /// <summary>
    /// Data created before accounts were separated has no owner. It all belonged to the original (first) account,
    /// so give it to that user. Runs on every start but only touches rows that still have no owner.
    /// </summary>
    public static async Task AssignUnownedDataAsync(LendingDbContext db, ILogger logger)
    {
        var firstUser = await db.Users.OrderBy(u => u.CreatedAt).Select(u => new { u.Id, u.Email }).FirstOrDefaultAsync();
        if (firstUser is null) return;

        // Copy the Id straight from Users so it's stored in exactly the same text form EF uses.
        var total = 0;
        foreach (var table in OwnedTables)
            total += await db.Database.ExecuteSqlRawAsync(
                $"""UPDATE "{table}" SET "OwnerId" = (SELECT "Id" FROM "Users" ORDER BY "CreatedAt" LIMIT 1) WHERE "OwnerId" IS NULL;""");
        if (total > 0) logger.LogInformation("Assigned {Count} existing records to {Email}.", total, firstUser.Email);
    }

    private static async Task<HashSet<string>> Columns(LendingDbContext db, string table) =>
        (await db.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{table}')").ToListAsync())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
