using LendingApp.Api.Domain;

namespace LendingApp.Api.Features.Loans;

/// <summary>The figures that decide what a loan costs and how it's repaid.</summary>
public readonly record struct LoanTerms(decimal Principal, decimal Rate, InterestType Type, int Term, TermUnit Unit)
{
    private int PeriodsPerYear => Unit == TermUnit.Weeks ? 52 : 12;

    public decimal TotalDue
    {
        get
        {
            if (Type == InterestType.Flat) return Math.Round(Principal * (1 + Rate / 100m), 2);
            if (Rate == 0) return Principal;
            return Installment * Term;
        }
    }

    /// <summary>Amount due each week or month.</summary>
    public decimal Installment
    {
        get
        {
            if (Term <= 0) return 0;
            if (Type == InterestType.Flat || Rate == 0) return Math.Round(TotalDue / Term, 2);

            // Standard amortization: P·r / (1 − (1 + r)^−n), r = rate per period.
            var r = (double)Rate / 100d / PeriodsPerYear;
            var payment = (double)Principal * r / (1 - Math.Pow(1 + r, -Term));
            return Math.Round((decimal)payment, 2);
        }
    }

    public DateOnly MaturityDate(DateOnly start) =>
        Unit == TermUnit.Weeks ? start.AddDays(7 * Term) : start.AddMonths(Term);

    public decimal Balance(decimal paid) => Math.Max(0, TotalDue - paid);
}

public static class LoanMath
{
    public static LoanTerms Terms(this Loan l) => new(l.Principal, l.InterestRate, l.InterestType, l.Term, l.TermUnit);

    /// <summary>Loans whose balance still counts as money owed (not pending approval, cancelled or paid off).</summary>
    public static bool CountsAsOwed(LoanStatus s) => s is LoanStatus.Active or LoanStatus.Defaulted;
}
