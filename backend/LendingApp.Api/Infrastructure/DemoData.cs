using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Features.Borrowers;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Infrastructure;

/// <summary>Seeds a few records through the normal commands, so they show up in history like any other change.</summary>
public static class DemoData
{
    public static async Task SeedAsync(IDispatcher d, LendingDbContext db)
    {
        if (await db.Borrowers.AnyAsync()) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var maria = await d.Send(new CreateBorrowerCommand(new("Maria", "Santos", "maria.santos@example.com", "+63 917 555 0101", "Quezon City")));
        var james = await d.Send(new CreateBorrowerCommand(new("James", "Reyes", "james.reyes@example.com", "+63 917 555 0102", "Makati")));
        await d.Send(new CreateBorrowerCommand(new("Ana", "Cruz", "ana.cruz@example.com", null, "Cebu City")));

        var loan1 = await d.Send(new CreateLoanCommand(new(maria, 50_000m, 12m, 12, today.AddMonths(-3), LoanStatus.Active, "Small business expansion")));
        await d.Send(new RecordPaymentCommand(loan1, new(4_442.44m, today.AddMonths(-2), "1st installment")));
        await d.Send(new RecordPaymentCommand(loan1, new(4_442.44m, today.AddMonths(-1), "2nd installment")));

        await d.Send(new CreateLoanCommand(new(james, 20_000m, 10m, 6, today, LoanStatus.Pending, "Motorcycle purchase")));
    }
}
