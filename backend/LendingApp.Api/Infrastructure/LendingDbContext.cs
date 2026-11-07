using System.Globalization;
using System.Text.Json;
using LendingApp.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LendingApp.Api.Infrastructure;

public class LendingDbContext(DbContextOptions<LendingDbContext> options, ICurrentUser currentUser) : DbContext(options)
{
    public DbSet<Borrower> Borrowers => Set<Borrower>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AppUser> Users => Set<AppUser>();

    /// <summary>
    /// The signed-in user. Every query on owned data is filtered to this user's rows (null ⇒ no rows),
    /// so handlers can't forget to scope a query, and another user's IDs simply aren't found.
    /// </summary>
    public Guid? CurrentUserId => currentUser.UserId;

    // Fields that are noise in the history view.
    private static readonly HashSet<string> IgnoredFields = ["Id", "CreatedAt", "OwnerId"];

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no timezone-aware type; make sure values come back flagged as UTC.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Guid keys are assigned in code, so EF must treat new instances as inserts, not updates.
        b.Entity<Borrower>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Loan>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Payment>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Email).HasMaxLength(200);
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Borrower>(e =>
        {
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(200);
            // Unique per user; NULLs don't collide, so many borrowers can have no email.
            e.HasIndex(x => new { x.OwnerId, x.Email }).IsUnique();
            e.Property(x => x.Notes).HasMaxLength(1000);
        });

        // Row-level separation between users.
        b.Entity<Borrower>().HasQueryFilter(x => x.OwnerId == CurrentUserId);
        b.Entity<Loan>().HasQueryFilter(x => x.OwnerId == CurrentUserId);
        b.Entity<Payment>().HasQueryFilter(x => x.OwnerId == CurrentUserId);
        b.Entity<AuditEntry>().HasQueryFilter(x => x.OwnerId == CurrentUserId);
        b.Entity<Loan>().HasIndex(x => x.OwnerId);
        b.Entity<Payment>().HasIndex(x => x.OwnerId);
        b.Entity<AuditEntry>().HasIndex(x => x.OwnerId);

        b.Entity<Loan>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.InterestType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TermUnit).HasConversion<string>().HasMaxLength(10);
            e.HasOne(x => x.Borrower).WithMany(x => x.Loans).HasForeignKey(x => x.BorrowerId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Status);
        });

        b.Entity<Payment>(e =>
        {
            e.HasOne(x => x.Loan).WithMany(x => x.Payments).HasForeignKey(x => x.LoanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.LoanId);
            e.HasIndex(x => x.BorrowerId);
            e.HasIndex(x => x.Timestamp);
        });
    }

    /// <summary>
    /// Every save writes an <see cref="AuditEntry"/> per changed auditable entity, in the same transaction,
    /// so history can never drift from the data.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        ChangeTracker.DetectChanges();
        var now = DateTime.UtcNow;
        var audits = new List<AuditEntry>();

        // New records belong to whoever is signed in; nobody can save records into another user's data.
        foreach (var entry in ChangeTracker.Entries<IOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity is not AuditEntry)
                entry.Entity.OwnerId = CurrentUserId ?? throw new InvalidOperationException("Saving data requires a signed-in user.");
            else if (entry.State == EntityState.Modified && entry.Property(x => x.OwnerId).IsModified)
                throw new InvalidOperationException("A record's owner can't be changed.");
        }

        foreach (var entry in ChangeTracker.Entries<IAuditable>().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var changes = CollectChanges(entry);
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            var action = entry.State switch
            {
                EntityState.Added => AuditAction.Created,
                EntityState.Deleted => AuditAction.Deleted,
                _ => AuditAction.Updated
            };

            var (loanId, borrowerId) = await ResolveRelatedIds(entry.Entity, ct);

            audits.Add(new AuditEntry
            {
                OwnerId = ((IOwned)entry.Entity).OwnerId,
                EntityType = entry.Entity.GetType().Name,
                EntityId = entry.Entity.Id,
                LoanId = loanId,
                BorrowerId = borrowerId,
                Action = action,
                Changes = JsonSerializer.Serialize(changes, JsonSerializerOptions.Web),
                Summary = Summarize(entry.Entity, action, changes),
                Timestamp = now
            });
        }

        AuditEntries.AddRange(audits);
        return await base.SaveChangesAsync(ct);
    }

    private static List<FieldChange> CollectChanges(EntityEntry<IAuditable> entry)
    {
        var list = new List<FieldChange>();
        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (IgnoredFields.Contains(name)) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    if (p.CurrentValue is not null) list.Add(new(name, null, Format(p.CurrentValue)));
                    break;
                case EntityState.Deleted:
                    list.Add(new(name, Format(p.OriginalValue), null));
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    list.Add(new(name, Format(p.OriginalValue), Format(p.CurrentValue)));
                    break;
            }
        }
        return list;
    }

    private async Task<(Guid? LoanId, Guid? BorrowerId)> ResolveRelatedIds(IAuditable entity, CancellationToken ct)
    {
        switch (entity)
        {
            case Borrower b:
                return (null, b.Id);
            case Loan l:
                return (l.Id, l.BorrowerId);
            case Payment p:
                var borrowerId = p.Loan?.BorrowerId
                    ?? Loans.Local.FirstOrDefault(l => l.Id == p.LoanId)?.BorrowerId
                    ?? await Loans.AsNoTracking().Where(l => l.Id == p.LoanId)
                        .Select(l => (Guid?)l.BorrowerId).FirstOrDefaultAsync(ct);
                return (p.LoanId, borrowerId);
            default:
                return (null, null);
        }
    }

    private static string Summarize(IAuditable entity, AuditAction action, List<FieldChange> changes)
    {
        var verb = action.ToString().ToLowerInvariant();
        var subject = entity switch
        {
            Borrower b => $"Borrower {$"{b.FirstName} {b.LastName}".Trim()}",
            Loan l => $"Loan of {l.Principal.ToString("N2", CultureInfo.InvariantCulture)}",
            Payment p => $"Payment of {p.Amount.ToString("N2", CultureInfo.InvariantCulture)}",
            _ => entity.GetType().Name
        };
        if (action != AuditAction.Updated) return $"{subject} {verb}";
        return $"{subject} updated: {string.Join(", ", changes.Select(c => c.Field))}";
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

public record FieldChange(string Field, string? OldValue, string? NewValue);
