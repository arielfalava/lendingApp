using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.Payments;

public record PaymentInput(decimal Amount, DateOnly PaidOn, string? Note);

// ───────────── Commands (write side) ─────────────

public record RecordPaymentCommand(Guid LoanId, PaymentInput Input) : ICommand<Guid>;
public record UpdatePaymentCommand(Guid Id, PaymentInput Input) : ICommand<Unit>;
public record DeletePaymentCommand(Guid Id) : ICommand<Unit>;

internal static class PaymentRules
{
    private static readonly LoanStatus[] PayableStatuses = [LoanStatus.Active, LoanStatus.Defaulted, LoanStatus.PaidOff];

    public static async Task<Loan> LoadLoan(LendingDbContext db, Guid loanId, CancellationToken ct)
        => await db.Loans.Include(l => l.Payments).FirstOrDefaultAsync(l => l.Id == loanId, ct)
           ?? throw new NotFoundException("Loan", loanId);

    /// <summary>Validates against the balance excluding <paramref name="excludePaymentId"/> (the payment being edited).</summary>
    public static void Validate(Loan loan, PaymentInput i, Guid? excludePaymentId)
    {
        var totalDue = loan.Terms().TotalDue;
        var paidElsewhere = loan.Payments.Where(p => p.Id != excludePaymentId).Sum(p => p.Amount);
        var remaining = totalDue - paidElsewhere;

        new Rules()
            .Require(PayableStatuses.Contains(loan.Status), "loan", $"Payments can't be recorded on a {loan.Status} loan. Activate it first.")
            .Require(i.Amount > 0, "amount", "Amount must be greater than zero.")
            .Require(i.Amount <= remaining, "amount", $"Amount exceeds the remaining balance of {remaining:N2}.")
            .ThrowIfInvalid();
    }

    /// <summary>Keeps the loan status consistent with its balance: fully paid ⇒ PaidOff, reopened ⇒ Active.</summary>
    public static void SyncStatus(Loan loan)
    {
        var balance = loan.Terms().TotalDue - loan.Payments.Sum(p => p.Amount);
        if (balance <= 0 && loan.Status is LoanStatus.Active or LoanStatus.Defaulted)
            loan.Status = LoanStatus.PaidOff;
        else if (balance > 0 && loan.Status == LoanStatus.PaidOff)
            loan.Status = LoanStatus.Active;
    }

    public static void Apply(Payment p, PaymentInput i)
    {
        p.Amount = Math.Round(i.Amount, 2);
        p.PaidOn = i.PaidOn;
        p.Note = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim();
    }
}

public class RecordPaymentHandler(LendingDbContext db) : ICommandHandler<RecordPaymentCommand, Guid>
{
    public async Task<Guid> Handle(RecordPaymentCommand c, CancellationToken ct)
    {
        var loan = await PaymentRules.LoadLoan(db, c.LoanId, ct);
        PaymentRules.Validate(loan, c.Input, null);

        var payment = new Payment { LoanId = loan.Id };
        PaymentRules.Apply(payment, c.Input);
        // Adding to the tracked loan's collection is enough: EF inserts it (Guid keys aren't store-generated).
        // Don't also call db.Payments.Add — EF's fix-up would put it in loan.Payments a second time
        // and SyncStatus would count the payment twice.
        loan.Payments.Add(payment);
        PaymentRules.SyncStatus(loan);

        await db.SaveChangesAsync(ct);
        return payment.Id;
    }
}

public class UpdatePaymentHandler(LendingDbContext db) : ICommandHandler<UpdatePaymentCommand, Unit>
{
    public async Task<Unit> Handle(UpdatePaymentCommand c, CancellationToken ct)
    {
        var loanId = await db.Payments.Where(p => p.Id == c.Id).Select(p => (Guid?)p.LoanId).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Payment", c.Id);
        var loan = await PaymentRules.LoadLoan(db, loanId, ct);
        var payment = loan.Payments.First(p => p.Id == c.Id);

        PaymentRules.Validate(loan, c.Input, payment.Id);
        PaymentRules.Apply(payment, c.Input);
        PaymentRules.SyncStatus(loan);

        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public class DeletePaymentHandler(LendingDbContext db) : ICommandHandler<DeletePaymentCommand, Unit>
{
    public async Task<Unit> Handle(DeletePaymentCommand c, CancellationToken ct)
    {
        var loanId = await db.Payments.Where(p => p.Id == c.Id).Select(p => (Guid?)p.LoanId).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Payment", c.Id);
        var loan = await PaymentRules.LoadLoan(db, loanId, ct);
        var payment = loan.Payments.First(p => p.Id == c.Id);

        loan.Payments.Remove(payment);
        db.Payments.Remove(payment);
        PaymentRules.SyncStatus(loan);

        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ───────────── Endpoints ─────────────

public static class PaymentEndpoints
{
    public static void MapPayments(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/loans/{loanId:guid}/payments", async (Guid loanId, PaymentInput input, IDispatcher d, CancellationToken ct) =>
        {
            var id = await d.Send(new RecordPaymentCommand(loanId, input), ct);
            return Results.Created($"/api/payments/{id}", new { id });
        }).WithTags("Payments");

        var g = app.MapGroup("/api/payments").WithTags("Payments");
        g.MapPut("/{id:guid}", async (Guid id, PaymentInput input, IDispatcher d, CancellationToken ct) =>
        {
            await d.Send(new UpdatePaymentCommand(id, input), ct);
            return Results.NoContent();
        });
        g.MapDelete("/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
        {
            await d.Send(new DeletePaymentCommand(id), ct);
            return Results.NoContent();
        });
    }
}
