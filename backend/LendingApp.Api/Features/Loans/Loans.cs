using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.Loans;

// ───────────── DTOs ─────────────

public record LoanSummaryDto(
    Guid Id, Guid BorrowerId, string BorrowerName,
    decimal Principal, decimal InterestRate, InterestType InterestType, int TermMonths, DateOnly StartDate, DateOnly MaturityDate,
    LoanStatus Status, string? Notes, DateTime CreatedAt,
    decimal MonthlyPayment, decimal TotalDue, decimal TotalPaid, decimal Balance, int PaymentCount);

public record PaymentDto(Guid Id, Guid LoanId, decimal Amount, DateOnly PaidOn, string? Note, DateTime CreatedAt);

public record LoanDetailDto(LoanSummaryDto Loan, List<PaymentDto> Payments);

public record LoanInput(
    Guid BorrowerId, decimal Principal, decimal InterestRate, int TermMonths, DateOnly StartDate,
    LoanStatus Status, string? Notes, InterestType InterestType = InterestType.Amortized);

// ───────────── Commands (write side) ─────────────

public record CreateLoanCommand(LoanInput Input) : ICommand<Guid>;
public record UpdateLoanCommand(Guid Id, LoanInput Input) : ICommand<Unit>;
public record DeleteLoanCommand(Guid Id) : ICommand<Unit>;

internal static class LoanRules
{
    public static async Task Validate(LendingDbContext db, LoanInput i, CancellationToken ct)
    {
        var borrowerExists = await db.Borrowers.AnyAsync(b => b.Id == i.BorrowerId, ct);
        new Rules()
            .Require(borrowerExists, "borrowerId", "Select an existing borrower.")
            .Require(i.Principal > 0, "principal", "Principal must be greater than zero.")
            .Require(i.InterestRate is >= 0 and <= 100, "interestRate", "Interest rate must be between 0 and 100%.")
            .Require(i.TermMonths is >= 1 and <= 600, "termMonths", "Term must be between 1 and 600 months.")
            .Require(Enum.IsDefined(i.Status), "status", "Unknown status.")
            .Require(Enum.IsDefined(i.InterestType), "interestType", "Unknown interest type.")
            .ThrowIfInvalid();
    }

    public static void Apply(Loan l, LoanInput i)
    {
        l.BorrowerId = i.BorrowerId;
        l.Principal = Math.Round(i.Principal, 2);
        l.InterestRate = i.InterestRate;
        l.InterestType = i.InterestType;
        l.TermMonths = i.TermMonths;
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
        LoanRules.Apply(loan, c.Input);
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
        LoanRules.Apply(loan, c.Input);
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
        int TermMonths, DateOnly StartDate, LoanStatus Status, string? Notes, DateTime CreatedAt, List<decimal> PaymentAmounts);

    public static IQueryable<Row> Rows(IQueryable<Loan> loans) => loans.Select(l => new Row(
        l.Id, l.BorrowerId, (l.Borrower!.FirstName + " " + l.Borrower.LastName).Trim(), l.Principal, l.InterestRate,
        l.InterestType, l.TermMonths, l.StartDate, l.Status, l.Notes, l.CreatedAt, l.Payments.Select(p => p.Amount).ToList()));

    public static LoanSummaryDto ToDto(Row r)
    {
        var monthly = LoanMath.MonthlyPayment(r.Principal, r.InterestRate, r.TermMonths, r.InterestType);
        var totalDue = LoanMath.TotalDue(r.Principal, r.InterestRate, r.TermMonths, r.InterestType);
        var paid = r.PaymentAmounts.Sum();
        return new LoanSummaryDto(
            r.Id, r.BorrowerId, r.BorrowerName, r.Principal, r.InterestRate, r.InterestType, r.TermMonths, r.StartDate,
            r.StartDate.AddMonths(r.TermMonths), r.Status, r.Notes, r.CreatedAt,
            monthly, totalDue, paid, Math.Max(0, totalDue - paid), r.PaymentAmounts.Count);
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
