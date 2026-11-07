using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.Loans;

// ───────────── DTOs ─────────────

public record LoanSummaryDto(
    Guid Id, Guid BorrowerId, string BorrowerName,
    decimal Principal, decimal InterestRate, InterestType InterestType, int Term, TermUnit TermUnit,
    DateOnly StartDate, DateOnly? MaturityDate, LoanStatus Status, string? Notes, DateTime CreatedAt,
    decimal? Installment, decimal TotalDue, decimal TotalPaid, decimal Balance, int PaymentCount);

public record PaymentDto(Guid Id, Guid LoanId, decimal Amount, DateOnly PaidOn, string? Note, DateTime CreatedAt);

public record LoanDetailDto(LoanSummaryDto Loan, List<PaymentDto> Payments);

/// <param name="BorrowerId">An existing borrower; leave empty and set <paramref name="NewBorrowerName"/> to create one.</param>
/// <param name="NewBorrowerName">Creates a borrower with this name in the same save as the loan.</param>
public record LoanInput(
    Guid? BorrowerId, decimal Principal, decimal InterestRate, int Term, DateOnly StartDate,
    LoanStatus Status, string? Notes, InterestType InterestType = InterestType.Flat,
    TermUnit TermUnit = TermUnit.Months, string? NewBorrowerName = null);

// ───────────── Commands (write side) ─────────────

public record CreateLoanCommand(LoanInput Input) : ICommand<Guid>;
public record UpdateLoanCommand(Guid Id, LoanInput Input) : ICommand<Unit>;
public record DeleteLoanCommand(Guid Id) : ICommand<Unit>;

internal static class LoanRules
{
    public static async Task Validate(LendingDbContext db, LoanInput i, CancellationToken ct)
    {
        var newName = i.NewBorrowerName?.Trim();
        var borrowerOk = !string.IsNullOrEmpty(newName)
            || (i.BorrowerId is { } id && await db.Borrowers.AnyAsync(b => b.Id == id, ct));
        var noDeadline = i.TermUnit == TermUnit.NoDeadline;
        // About 10 years in days or weeks; 50 years in months.
        var maxTerm = i.TermUnit switch { TermUnit.Days => 3650, TermUnit.Weeks => 520, _ => 600 };

        new Rules()
            .Require(borrowerOk, "borrowerId", "Select a borrower or enter a new borrower's name.")
            .Require(i.Principal > 0, "principal", "Principal must be greater than zero.")
            .Require(i.InterestRate is >= 0 and <= 100, "interestRate", "Interest rate must be between 0 and 100%.")
            .Require(Enum.IsDefined(i.TermUnit), "termUnit", "Choose days, weeks, months or no specific deadline.")
            .Require(noDeadline || (i.Term >= 1 && i.Term <= maxTerm), "term",
                $"Term must be between 1 and {maxTerm} {i.TermUnit.ToString().ToLowerInvariant()}.")
            .Require(Enum.IsDefined(i.Status), "status", "Unknown status.")
            .Require(Enum.IsDefined(i.InterestType), "interestType", "Unknown interest type.")
            .ThrowIfInvalid();
    }

    /// <summary>Copies the input onto the loan, creating the borrower first when a new name was given.</summary>
    public static void Apply(LendingDbContext db, Loan l, LoanInput i)
    {
        var newName = i.NewBorrowerName?.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            var borrower = new Borrower { FirstName = newName };
            db.Borrowers.Add(borrower);
            l.BorrowerId = borrower.Id;
        }
        else
        {
            l.BorrowerId = i.BorrowerId!.Value;
        }

        l.Principal = Math.Round(i.Principal, 2);
        l.InterestRate = i.InterestRate;
        l.InterestType = i.InterestType;
        l.Term = i.TermUnit == TermUnit.NoDeadline ? 0 : i.Term;
        l.TermUnit = i.TermUnit;
        l.StartDate = i.StartDate;
        l.Status = i.Status;
        l.Notes = string.IsNullOrWhiteSpace(i.Notes) ? null : i.Notes.Trim();
    }
}

public class CreateLoanHandler(LendingDbContext db) : ICommandHandler<CreateLoanCommand, Guid>
{
    public async Task<Guid> Handle(CreateLoanCommand c, CancellationToken ct)
    {
        await LoanRules.Validate(db, c.Input, ct);
        var loan = new Loan();
        LoanRules.Apply(db, loan, c.Input);
        db.Loans.Add(loan);
        await db.SaveChangesAsync(ct);
        return loan.Id;
    }
}

public class UpdateLoanHandler(LendingDbContext db) : ICommandHandler<UpdateLoanCommand, Unit>
{
    public async Task<Unit> Handle(UpdateLoanCommand c, CancellationToken ct)
    {
        var loan = await db.Loans.FindAsync([c.Id], ct) ?? throw new NotFoundException("Loan", c.Id);
        await LoanRules.Validate(db, c.Input, ct);
        LoanRules.Apply(db, loan, c.Input);
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public class DeleteLoanHandler(LendingDbContext db) : ICommandHandler<DeleteLoanCommand, Unit>
{
    public async Task<Unit> Handle(DeleteLoanCommand c, CancellationToken ct)
    {
        // Load payments too, so each cascaded payment delete is recorded in history.
        var loan = await db.Loans.Include(l => l.Payments).FirstOrDefaultAsync(l => l.Id == c.Id, ct)
                   ?? throw new NotFoundException("Loan", c.Id);
        db.Payments.RemoveRange(loan.Payments);
        db.Loans.Remove(loan);
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ───────────── Queries (read side) ─────────────

public record GetLoansQuery(LoanStatus? Status, Guid? BorrowerId, string? Search) : IQuery<List<LoanSummaryDto>>;
public record GetLoanQuery(Guid Id) : IQuery<LoanDetailDto>;

internal static class LoanReadModel
{
    // SQLite can't aggregate decimals server-side, so amounts are pulled and summed in memory.
    public sealed record Row(
        Guid Id, Guid BorrowerId, string BorrowerName, decimal Principal, decimal InterestRate, InterestType InterestType,
        int Term, TermUnit TermUnit, DateOnly StartDate, LoanStatus Status, string? Notes, DateTime CreatedAt,
        List<decimal> PaymentAmounts)
    {
        public LoanTerms Terms => new(Principal, InterestRate, InterestType, Term, TermUnit);
    }

    public static IQueryable<Row> Rows(IQueryable<Loan> loans) => loans.Select(l => new Row(
        l.Id, l.BorrowerId, (l.Borrower!.FirstName + " " + l.Borrower.LastName).Trim(), l.Principal, l.InterestRate,
        l.InterestType, l.Term, l.TermUnit, l.StartDate, l.Status, l.Notes, l.CreatedAt, l.Payments.Select(p => p.Amount).ToList()));

    public static LoanSummaryDto ToDto(Row r)
    {
        var terms = r.Terms;
        var paid = r.PaymentAmounts.Sum();
        return new LoanSummaryDto(
            r.Id, r.BorrowerId, r.BorrowerName, r.Principal, r.InterestRate, r.InterestType, r.Term, r.TermUnit,
            r.StartDate, terms.MaturityDate(r.StartDate), r.Status, r.Notes, r.CreatedAt,
            terms.Installment, terms.TotalDue, paid, terms.Balance(paid), r.PaymentAmounts.Count);
    }
}

public class GetLoansHandler(LendingDbContext db) : IQueryHandler<GetLoansQuery, List<LoanSummaryDto>>
{
    public async Task<List<LoanSummaryDto>> Handle(GetLoansQuery q, CancellationToken ct)
    {
        var loans = db.Loans.AsNoTracking();
        if (q.Status is { } status) loans = loans.Where(l => l.Status == status);
        if (q.BorrowerId is { } borrowerId) loans = loans.Where(l => l.BorrowerId == borrowerId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            loans = loans.Where(l => (l.Borrower!.FirstName + " " + l.Borrower.LastName).ToLower().Contains(s)
                                     || (l.Notes != null && l.Notes.ToLower().Contains(s)));
        }

        var rows = await LoanReadModel.Rows(loans.OrderByDescending(l => l.CreatedAt)).ToListAsync(ct);
        return rows.Select(LoanReadModel.ToDto).ToList();
    }
}

public class GetLoanHandler(LendingDbContext db) : IQueryHandler<GetLoanQuery, LoanDetailDto>
{
    public async Task<LoanDetailDto> Handle(GetLoanQuery q, CancellationToken ct)
    {
        var row = await LoanReadModel.Rows(db.Loans.AsNoTracking().Where(l => l.Id == q.Id)).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Loan", q.Id);

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.LoanId == q.Id)
            .OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.CreatedAt)
            .Select(p => new PaymentDto(p.Id, p.LoanId, p.Amount, p.PaidOn, p.Note, p.CreatedAt))
            .ToListAsync(ct);

        return new LoanDetailDto(LoanReadModel.ToDto(row), payments);
    }
}

// ───────────── Endpoints ─────────────

public static class LoanEndpoints
{
    public static void MapLoans(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/loans").WithTags("Loans");

        g.MapGet("/", (LoanStatus? status, Guid? borrowerId, string? search, IDispatcher d, CancellationToken ct)
            => d.Query(new GetLoansQuery(status, borrowerId, search), ct));
        g.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => d.Query(new GetLoanQuery(id), ct));

        g.MapPost("/", async (LoanInput input, IDispatcher d, CancellationToken ct) =>
        {
            var id = await d.Send(new CreateLoanCommand(input), ct);
            return Results.Created($"/api/loans/{id}", new { id });
        });
        g.MapPut("/{id:guid}", async (Guid id, LoanInput input, IDispatcher d, CancellationToken ct) =>
        {
            await d.Send(new UpdateLoanCommand(id, input), ct);
            return Results.NoContent();
        });
        g.MapDelete("/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
        {
            await d.Send(new DeleteLoanCommand(id), ct);
            return Results.NoContent();
        });
    }
}
