using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.Dashboard;

public record DashboardDto(
    int BorrowerCount, int LoanCount, Dictionary<string, int> LoansByStatus,
    decimal TotalLent, decimal TotalCollected, decimal OutstandingBalance);

public record GetDashboardQuery : IQuery<DashboardDto>;

public class GetDashboardHandler(LendingDbContext db) : IQueryHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<DashboardDto> Handle(GetDashboardQuery q, CancellationToken ct)
    {
        var borrowers = await db.Borrowers.CountAsync(ct);
        var loans = await db.Loans.AsNoTracking()
            .Select(l => new { l.Principal, l.InterestRate, l.InterestType, l.TermMonths, l.Status, Paid = l.Payments.Select(p => p.Amount).ToList() })
            .ToListAsync(ct);

        var lending = loans.Where(l => l.Status is not (LoanStatus.Pending or LoanStatus.Cancelled)).ToList();
        var outstanding = lending
            .Where(l => l.Status is LoanStatus.Active or LoanStatus.Defaulted)
            .Sum(l => Math.Max(0, LoanMath.TotalDue(l.Principal, l.InterestRate, l.TermMonths, l.InterestType) - l.Paid.Sum()));

        return new DashboardDto(
            borrowers,
            loans.Count,
            Enum.GetNames<LoanStatus>().ToDictionary(s => s, s => loans.Count(l => l.Status.ToString() == s)),
            lending.Sum(l => l.Principal),
            loans.Sum(l => l.Paid.Sum()),
            outstanding);
    }
}

public static class DashboardEndpoints
{
    public static void MapDashboard(this IEndpointRouteBuilder app)
        => app.MapGet("/api/dashboard", (IDispatcher d, CancellationToken ct) => d.Query(new GetDashboardQuery(), ct))
            .WithTags("Dashboard");
}
