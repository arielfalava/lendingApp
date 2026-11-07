namespace LendingApp.Api.Domain;

/// <summary>Records that belong to one user. Each user only ever sees and changes their own (see LendingDbContext).</summary>
public interface IOwned
{
    Guid OwnerId { get; set; }
}

/// <summary>Entities implementing this are tracked in the history (audit) log automatically.</summary>
public interface IAuditable
{
    Guid Id { get; }
}

public class Borrower : IAuditable, IOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Loan> Loans { get; set; } = [];
}

public enum LoanStatus
{
    Pending,
    Active,
    PaidOff,
    Defaulted,
    Cancelled
}

public enum InterestType
{
    /// <summary>Annual rate, repaid in equal monthly installments (bank-style amortization).</summary>
    Amortized,
    /// <summary>Rate is charged once on the principal: total owed = principal × (1 + rate%).</summary>
    Flat
}

/// <summary>What one unit of <see cref="Loan.Term"/> means, and how often installments fall due.</summary>
public enum TermUnit
{
    Weeks,
    Months,
    Days,
    /// <summary>No schedule or due date: the borrower repays whenever they can. <see cref="Loan.Term"/> is 0.</summary>
    NoDeadline
}

public class Loan : IAuditable, IOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid BorrowerId { get; set; }
    public Borrower? Borrower { get; set; }

    public decimal Principal { get; set; }
    /// <summary>Interest rate in percent. Per year for <see cref="InterestType.Amortized"/>; one-time for <see cref="InterestType.Flat"/>.</summary>
    public decimal InterestRate { get; set; }
    public InterestType InterestType { get; set; } = InterestType.Flat;
    /// <summary>Number of installments, each one <see cref="TermUnit"/> apart.</summary>
    public int Term { get; set; }
    public TermUnit TermUnit { get; set; } = TermUnit.Months;
    public DateOnly StartDate { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Pending;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Payment> Payments { get; set; } = [];
}

public class Payment : IAuditable, IOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid LoanId { get; set; }
    public Loan? Loan { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Someone who can sign in, with Google and/or a password. Only a salted hash of a password is stored.</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    /// <summary>Empty for accounts that only sign in with Google.</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>Google's stable account id ("sub"), set the first time the user signs in with Google.</summary>
    public string? GoogleSubject { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum AuditAction
{
    Created,
    Updated,
    Deleted
}

/// <summary>One row of history: what changed, on which record, and when.</summary>
public class AuditEntry : IOwned
{
    public long Id { get; set; }
    public Guid OwnerId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    /// <summary>The loan this change relates to (if any), so a loan's full history includes its payments.</summary>
    public Guid? LoanId { get; set; }
    /// <summary>The borrower this change relates to (if any).</summary>
    public Guid? BorrowerId { get; set; }
    public AuditAction Action { get; set; }
    /// <summary>JSON array of { field, oldValue, newValue }.</summary>
    public string Changes { get; set; } = "[]";
    public string Summary { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
