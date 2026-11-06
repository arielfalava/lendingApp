using LendingApp.Api.Domain;

namespace LendingApp.Api.Features.Loans;

/// <summary>Repayment arithmetic for both interest types.</summary>
public static class LoanMath
{
    public static decimal TotalDue(decimal principal, decimal ratePercent, int termMonths, InterestType type)
    {
        if (type == InterestType.Flat) return Math.Round(principal * (1 + ratePercent / 100m), 2);
        if (ratePercent == 0) return principal;
        return MonthlyPayment(principal, ratePercent, termMonths, type) * termMonths;
    }

    public static decimal MonthlyPayment(decimal principal, decimal ratePercent, int termMonths, InterestType type)
    {
        if (termMonths <= 0) return 0;
        if (type == InterestType.Flat || ratePercent == 0)
            return Math.Round(TotalDue(principal, ratePercent, termMonths, type) / termMonths, 2);

        // Standard amortization: P·r / (1 − (1 + r)^−n), r = monthly rate.
        var r = (double)ratePercent / 100d / 12d;
        var payment = (double)principal * r / (1 - Math.Pow(1 + r, -termMonths));
        return Math.Round((decimal)payment, 2);
    }

    public static decimal TotalDue(Loan l) => TotalDue(l.Principal, l.InterestRate, l.TermMonths, l.InterestType);
}
