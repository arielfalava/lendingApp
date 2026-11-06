using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.Borrowers;

// ───────────── DTOs ─────────────

public record BorrowerDto(
    Guid Id, string FirstName, string LastName, string? Email, string? Phone, string? Address, string? Notes,
    DateTime CreatedAt, int LoanCount, int ActiveLoanCount);

public record BorrowerInput(
    string FirstName, string? LastName, string? Email, string? Phone, string? Address, string? Notes = null);

// ───────────── Commands (write side) ─────────────

public record CreateBorrowerCommand(BorrowerInput Input) : ICommand<Guid>;
public record UpdateBorrowerCommand(Guid Id, BorrowerInput Input) : ICommand<Unit>;
public record DeleteBorrowerCommand(Guid Id) : ICommand<Unit>;

internal static class BorrowerRules
{
    public static async Task Validate(LendingDbContext db, BorrowerInput i, Guid? existingId, CancellationToken ct)
    {
        // Email is optional; when given it must look valid and be unique.
        var email = Clean(i.Email);
        var emailTaken = email is not null &&
            await db.Borrowers.AnyAsync(b => b.Email == email && b.Id != existingId, ct);

        new Rules()
            .Require(!string.IsNullOrWhiteSpace(i.FirstName), "firstName", "Name is required.")
            .Require(email is null || email.Contains('@'), "email", "Enter a valid email or leave it blank.")
            .Require(!emailTaken, "email", "Another borrower already uses this email.")
            .ThrowIfInvalid();
    }

    public static void Apply(Borrower b, BorrowerInput i)
    {
        b.FirstName = i.FirstName.Trim();
        b.LastName = i.LastName?.Trim() ?? "";
        b.Email = Clean(i.Email);
        b.Phone = Clean(i.Phone);
        b.Address = Clean(i.Address);
        b.Notes = Clean(i.Notes);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class CreateBorrowerHandler(LendingDbContext db) : ICommandHandler<CreateBorrowerCommand, Guid>
{
    public async Task<Guid> Handle(CreateBorrowerCommand c, CancellationToken ct)
    {
        await BorrowerRules.Validate(db, c.Input, null, ct);
        var borrower = new Borrower();
        BorrowerRules.Apply(borrower, c.Input);
        db.Borrowers.Add(borrower);
        await db.SaveChangesAsync(ct);
        return borrower.Id;
    }
}

public class UpdateBorrowerHandler(LendingDbContext db) : ICommandHandler<UpdateBorrowerCommand, Unit>
{
    public async Task<Unit> Handle(UpdateBorrowerCommand c, CancellationToken ct)
    {
        var borrower = await db.Borrowers.FindAsync([c.Id], ct) ?? throw new NotFoundException("Borrower", c.Id);
        await BorrowerRules.Validate(db, c.Input, c.Id, ct);
        BorrowerRules.Apply(borrower, c.Input);
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public class DeleteBorrowerHandler(LendingDbContext db) : ICommandHandler<DeleteBorrowerCommand, Unit>
{
    public async Task<Unit> Handle(DeleteBorrowerCommand c, CancellationToken ct)
    {
        var borrower = await db.Borrowers.FindAsync([c.Id], ct) ?? throw new NotFoundException("Borrower", c.Id);
        var hasLoans = await db.Loans.AnyAsync(l => l.BorrowerId == c.Id, ct);
        new Rules().Require(!hasLoans, "borrower", "Delete this borrower's loans first.").ThrowIfInvalid();
        db.Borrowers.Remove(borrower);
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ───────────── Queries (read side) ─────────────

public record GetBorrowersQuery(string? Search) : IQuery<List<BorrowerDto>>;
public record GetBorrowerQuery(Guid Id) : IQuery<BorrowerDto>;

public class GetBorrowersHandler(LendingDbContext db) : IQueryHandler<GetBorrowersQuery, List<BorrowerDto>>
{
    public async Task<List<BorrowerDto>> Handle(GetBorrowersQuery q, CancellationToken ct)
    {
        var query = db.Borrowers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(b => (b.FirstName + " " + b.LastName).ToLower().Contains(s)
                                     || (b.Email != null && b.Email.ToLower().Contains(s))
                                     || (b.Notes != null && b.Notes.ToLower().Contains(s)));
        }
        return await query
            .OrderBy(b => b.FirstName).ThenBy(b => b.LastName)
            .Select(BorrowerProjection.ToDto)
            .ToListAsync(ct);
    }
}

public class GetBorrowerHandler(LendingDbContext db) : IQueryHandler<GetBorrowerQuery, BorrowerDto>
{
    public async Task<BorrowerDto> Handle(GetBorrowerQuery q, CancellationToken ct)
        => await db.Borrowers.AsNoTracking().Where(b => b.Id == q.Id).Select(BorrowerProjection.ToDto).FirstOrDefaultAsync(ct)
           ?? throw new NotFoundException("Borrower", q.Id);
}

internal static class BorrowerProjection
{
    public static readonly System.Linq.Expressions.Expression<Func<Borrower, BorrowerDto>> ToDto = b => new BorrowerDto(
        b.Id, b.FirstName, b.LastName, b.Email, b.Phone, b.Address, b.Notes, b.CreatedAt,
        b.Loans.Count, b.Loans.Count(l => l.Status == LoanStatus.Active));
}

// ───────────── Endpoints ─────────────

public static class BorrowerEndpoints
{
    public static void MapBorrowers(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/borrowers").WithTags("Borrowers");

        g.MapGet("/", (string? search, IDispatcher d, CancellationToken ct) => d.Query(new GetBorrowersQuery(search), ct));
        g.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => d.Query(new GetBorrowerQuery(id), ct));

        g.MapPost("/", async (BorrowerInput input, IDispatcher d, CancellationToken ct) =>
        {
            var id = await d.Send(new CreateBorrowerCommand(input), ct);
            return Results.Created($"/api/borrowers/{id}", new { id });
        });
        g.MapPut("/{id:guid}", async (Guid id, BorrowerInput input, IDispatcher d, CancellationToken ct) =>
        {
            await d.Send(new UpdateBorrowerCommand(id, input), ct);
            return Results.NoContent();
        });
        g.MapDelete("/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
        {
            await d.Send(new DeleteBorrowerCommand(id), ct);
            return Results.NoContent();
        });
    }
}
