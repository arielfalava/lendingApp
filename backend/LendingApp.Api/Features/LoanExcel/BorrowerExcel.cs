using System.Globalization;
using ClosedXML.Excel;
using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.LoanExcel;

/// <summary>
/// One borrower's full record as a workbook: a "Borrower" sheet (details), a "Loans" sheet (one row per loan,
/// numbered by "Loan #") and a "Payments" sheet (every payment, pointing at its loan by "Loan #").
/// The import reads the same Loans and Payments sheets and adds them to the borrower; the Borrower sheet and the
/// computed columns (Total owed, Total paid, Remaining balance) are for reading only.
/// </summary>
internal static class BorrowerSheet
{
    public const string LoanNo = "Loan #";
    public const string InterestType = "Interest type";
    public const string Amount = "Amount";
    public const string PaidOn = "Paid on";
    public const string Note = "Note";

    public static readonly string[] LoanColumns =
        [LoanNo, LoanSheet.Principal, LoanSheet.Interest, InterestType, LoanSheet.Term, LoanSheet.TermUnit, LoanSheet.StartDate, LoanSheet.Status, LoanSheet.Remarks];
    public static readonly string[] LoanRequired = [LoanNo, LoanSheet.Principal, LoanSheet.Interest, LoanSheet.TermUnit, LoanSheet.StartDate];
    public static readonly string[] PaymentColumns = [LoanNo, Amount, PaidOn, Note];

    public const string PaymentsSheet = "Payments";

    public static Domain.InterestType? ParseInterestType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "" or "flat" => Domain.InterestType.Flat,
        "amortized" or "amortised" => Domain.InterestType.Amortized,
        _ => null
    };

    public static void Header(IXLWorksheet ws, IReadOnlyList<string> cols, IEnumerable<string> required)
    {
        for (var c = 0; c < cols.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = cols[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = required.Contains(cols[c]) ? XLColor.FromHtml("#DCE8FB") : XLColor.FromHtml("#EEF0F3");
        }
        ws.SheetView.FreezeRows(1);
    }

    public static Dictionary<string, int> HeaderMap(IXLWorksheet ws)
    {
        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in ws.Row(1).CellsUsed())
            cols.TryAdd(cell.GetString().Trim().TrimEnd('*').Trim(), cell.Address.ColumnNumber);
        return cols;
    }
}

// ───────────── Export (query) ─────────────

public record ExportBorrowerQuery(Guid BorrowerId) : IQuery<(string Name, byte[] File)>;

public class ExportBorrowerHandler(LendingDbContext db) : IQueryHandler<ExportBorrowerQuery, (string Name, byte[] File)>
{
    public async Task<(string Name, byte[] File)> Handle(ExportBorrowerQuery q, CancellationToken ct)
    {
        var b = await db.Borrowers.AsNoTracking()
                    .Include(x => x.Loans).ThenInclude(l => l.Payments)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(x => x.Id == q.BorrowerId, ct)
                ?? throw new NotFoundException("Borrower", q.BorrowerId);
        var loans = b.Loans.OrderBy(l => l.StartDate).ThenBy(l => l.CreatedAt).ToList();
        var name = $"{b.FirstName} {b.LastName}".Trim();

        using var wb = new XLWorkbook();

        // Borrower
        var info = wb.AddWorksheet("Borrower");
        decimal totalOwed = 0, totalPaid = 0, outstanding = 0;
        foreach (var l in loans)
        {
            var due = l.Terms().TotalDue;
            var paid = l.Payments.Sum(p => p.Amount);
            totalOwed += due;
            totalPaid += paid;
            if (LoanMath.CountsAsOwed(l.Status)) outstanding += Math.Max(due - paid, 0);
        }
        object?[][] details =
        [
            ["Name", name],
            ["Email", b.Email],
            ["Phone", b.Phone],
            ["Address", b.Address],
            ["Notes", b.Notes],
            ["Customer since", b.CreatedAt.ToLocalTime().Date],
            ["Loans", loans.Count],
            ["Payments", loans.Sum(l => l.Payments.Count)],
            ["Total owed (all loans)", totalOwed],
            ["Total paid", totalPaid],
            ["Remaining balance", outstanding],
            ["Exported", DateTime.Now],
        ];
        for (var r = 0; r < details.Length; r++)
        {
            info.Cell(r + 1, 1).Value = (string)details[r][0]!;
            info.Cell(r + 1, 1).Style.Font.Bold = true;
            info.Cell(r + 1, 2).Value = XLCellValue.FromObject(details[r][1]);
        }
        info.Cell(6, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        info.Cell(12, 2).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        info.Range(9, 2, 11, 2).Style.NumberFormat.Format = "#,##0.00";
        info.Column(1).Width = 24;
        info.Column(2).Width = 50;

        // Loans
        string[] extra = ["Total owed", "Total paid", "Remaining balance"];
        var ws = wb.AddWorksheet(LoanSheet.SheetName);
        var loanCols = BorrowerSheet.LoanColumns.Concat(extra).ToList();
        BorrowerSheet.Header(ws, loanCols, BorrowerSheet.LoanRequired);
        for (var c = loanCols.Count - extra.Length + 1; c <= loanCols.Count; c++)
            ws.Cell(1, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4D6");

        var lastLoanRow = Math.Max(loans.Count, 200) + 1;
        ws.Range(2, 4, lastLoanRow, 4).CreateDataValidation().List("\"Flat,Amortized\"");
        ws.Range(2, 6, lastLoanRow, 6).CreateDataValidation().List($"\"{string.Join(",", LoanSheet.TermUnitLabels)}\"");
        ws.Range(2, 8, lastLoanRow, 8).CreateDataValidation().List($"\"{string.Join(",", LoanSheet.StatusLabels)}\"");

        var r2 = 2;
        for (var i = 0; i < loans.Count; i++, r2++)
        {
            var l = loans[i];
            var due = l.Terms().TotalDue;
            var paid = l.Payments.Sum(p => p.Amount);
            ws.Cell(r2, 1).Value = i + 1;
            ws.Cell(r2, 2).Value = l.Principal;
            ws.Cell(r2, 3).Value = l.InterestRate;
            ws.Cell(r2, 4).Value = l.InterestType.ToString();
            if (l.TermUnit != TermUnit.NoDeadline) ws.Cell(r2, 5).Value = l.Term;
            ws.Cell(r2, 6).Value = LoanSheet.Label(l.TermUnit);
            ws.Cell(r2, 7).Value = l.StartDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r2, 8).Value = LoanSheet.Label(l.Status);
            ws.Cell(r2, 9).Value = l.Notes;
            ws.Cell(r2, 10).Value = due;
            ws.Cell(r2, 11).Value = paid;
            ws.Cell(r2, 12).Value = Math.Max(due - paid, 0);
        }
        ws.Range(2, 7, lastLoanRow, 7).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(2, 2, lastLoanRow, 2).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(2, 10, lastLoanRow, 12).Style.NumberFormat.Format = "#,##0.00";
        ws.Columns().AdjustToContents(1, Math.Min(r2, 300));
        ws.Column(9).Width = Math.Min(Math.Max(ws.Column(9).Width, 20), 50);

        // Payments
        var ps = wb.AddWorksheet(BorrowerSheet.PaymentsSheet);
        BorrowerSheet.Header(ps, BorrowerSheet.PaymentColumns, [BorrowerSheet.LoanNo, BorrowerSheet.Amount, BorrowerSheet.PaidOn]);
        var r3 = 2;
        for (var i = 0; i < loans.Count; i++)
        {
            foreach (var p in loans[i].Payments.OrderBy(p => p.PaidOn).ThenBy(p => p.CreatedAt))
            {
                ps.Cell(r3, 1).Value = i + 1;
                ps.Cell(r3, 2).Value = p.Amount;
                ps.Cell(r3, 3).Value = p.PaidOn.ToDateTime(TimeOnly.MinValue);
                ps.Cell(r3, 4).Value = p.Note;
                r3++;
            }
        }
        var lastPayRow = Math.Max(r3 - 1, 500);
        ps.Range(2, 2, lastPayRow, 2).Style.NumberFormat.Format = "#,##0.00";
        ps.Range(2, 3, lastPayRow, 3).Style.DateFormat.Format = "yyyy-mm-dd";
        ps.Column(1).Width = 10;
        ps.Column(2).Width = 14;
        ps.Column(3).Width = 14;
        ps.Column(4).Width = 40;

        var help = wb.AddWorksheet("How to import");
        string[] lines =
        [
            "Importing this file on a borrower's page ADDS the loans and payments below to that borrower. It does not change or remove existing loans.",
            "",
            "Loans sheet: one loan per row. Blue columns are required. \"Loan #\" is any number that identifies the row within this file.",
            "  Interest type: Flat (default) or Amortized.  Term unit: " + string.Join(", ", LoanSheet.TermUnitLabels) + ".  Status: " + string.Join(", ", LoanSheet.StatusLabels) + " (default Active).",
            "  Total owed, Total paid and Remaining balance are calculated and are ignored on import.",
            "Payments sheet: one payment per row. \"Loan #\" must match a row on the Loans sheet.",
            "  A loan's payments can't add up to more than its total owed. Pending and Cancelled loans can't have payments.",
            "  A loan whose payments cover the total owed becomes Paid off.",
            "",
            "If any row has a problem, nothing is imported and every problem is listed.",
        ];
        for (var r = 0; r < lines.Length; r++) help.Cell(r + 1, 1).Value = lines[r];
        help.Column(1).Width = 140;

        return (name, LoanSheet.ToBytes(wb));
    }
}

// ───────────── Import (command) ─────────────

public record BorrowerImportResult(int LoansCreated, int PaymentsCreated);

/// <summary>Adds the Loans and Payments sheets of a borrower workbook to one borrower. All rows or none.</summary>
public record ImportBorrowerCommand(Guid BorrowerId, Stream File) : ICommand<BorrowerImportResult>;

public class ImportBorrowerHandler(LendingDbContext db) : ICommandHandler<ImportBorrowerCommand, BorrowerImportResult>
{
    private const int MaxRows = 5000;

    public async Task<BorrowerImportResult> Handle(ImportBorrowerCommand c, CancellationToken ct)
    {
        var borrower = await db.Borrowers.FirstOrDefaultAsync(b => b.Id == c.BorrowerId, ct)
                       ?? throw new NotFoundException("Borrower", c.BorrowerId);

        XLWorkbook wb;
        try { wb = new XLWorkbook(c.File); }
        catch (Exception) { throw Invalid("That file isn't a readable Excel (.xlsx) workbook."); }

        using (wb)
        {
            var loanWs = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(LoanSheet.SheetName, StringComparison.OrdinalIgnoreCase))
                         ?? throw Invalid("The workbook has no \"Loans\" sheet. Use a file from Export on a borrower's page.");
            var payWs = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(BorrowerSheet.PaymentsSheet, StringComparison.OrdinalIgnoreCase));

            var cols = BorrowerSheet.HeaderMap(loanWs);
            var missing = BorrowerSheet.LoanRequired.Where(h => !cols.ContainsKey(h)).ToList();
            if (missing.Count > 0)
                throw Invalid($"Loans sheet is missing column(s): {string.Join(", ", missing)}. Use a file from Export on a borrower's page.");

            var errors = new List<string>();
            var loans = new Dictionary<int, Loan>();
            var badLoanNos = new HashSet<int>(); // loan rows that already have an error; their payments aren't reported again

            var lastRow = loanWs.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxRows) throw Invalid($"Too many loan rows ({lastRow - 1}). Import at most {MaxRows} at a time.");

            for (var r = 2; r <= lastRow; r++)
            {
                var row = loanWs.Row(r);
                string Text(string col) => cols.TryGetValue(col, out var i) ? row.Cell(i).GetFormattedString().Trim() : "";
                IXLCell? Cell(string col) => cols.TryGetValue(col, out var i) ? row.Cell(i) : null;
                if (BorrowerSheet.LoanColumns.All(h => Text(h).Length == 0)) continue;

                var rowErrors = new List<string>();
                var no = ImportLoansHandler.Number(Cell(BorrowerSheet.LoanNo));
                var principal = ImportLoansHandler.Number(Cell(LoanSheet.Principal));
                var rate = ImportLoansHandler.Number(Cell(LoanSheet.Interest));
                var type = BorrowerSheet.ParseInterestType(Text(BorrowerSheet.InterestType));
                var unit = LoanSheet.ParseTermUnit(Text(LoanSheet.TermUnit));
                var term = ImportLoansHandler.Number(Cell(LoanSheet.Term));
                var start = ImportLoansHandler.Date(Cell(LoanSheet.StartDate));
                var status = LoanSheet.ParseStatus(Text(LoanSheet.Status));

                if (no is not > 0 || no != Math.Floor(no.Value)) rowErrors.Add("Loan # must be a whole number above 0");
                else if (loans.ContainsKey((int)no.Value)) rowErrors.Add($"Loan # {no} is used on more than one row");
                if (principal is not > 0) rowErrors.Add("principal must be a number above 0");
                if (rate is not (>= 0 and <= 100)) rowErrors.Add("interest % must be a number from 0 to 100");
                if (type is null) rowErrors.Add("interest type must be Flat or Amortized");
                if (unit is null) rowErrors.Add($"term unit must be one of {string.Join(", ", LoanSheet.TermUnitLabels)}");
                else if (unit != TermUnit.NoDeadline && (term is not > 0 || term != Math.Floor(term.Value)))
                    rowErrors.Add($"term must be a whole number of {unit.ToString()!.ToLowerInvariant()}");
                if (start is null) rowErrors.Add("start date is missing or not a date");
                if (status is null) rowErrors.Add($"status must be one of {string.Join(", ", LoanSheet.StatusLabels)}");

                if (rowErrors.Count > 0)
                {
                    errors.Add($"Loans row {r}: {string.Join("; ", rowErrors)}.");
                    if (no is > 0 && no == Math.Floor(no.Value)) badLoanNos.Add((int)no.Value);
                    continue;
                }

                loans[(int)no!.Value] = new Loan
                {
                    BorrowerId = borrower.Id,
                    Principal = Math.Round(principal!.Value, 2),
                    InterestRate = rate!.Value,
                    InterestType = type!.Value,
                    Term = unit == TermUnit.NoDeadline ? 0 : (int)term!.Value,
                    TermUnit = unit!.Value,
                    StartDate = start!.Value,
                    Status = status!.Value,
                    Notes = Text(LoanSheet.Remarks) is { Length: > 0 } n ? n : null,
                };
            }

            var payments = 0;
            if (payWs is not null)
            {
                var pcols = BorrowerSheet.HeaderMap(payWs);
                var pmissing = new[] { BorrowerSheet.LoanNo, BorrowerSheet.Amount, BorrowerSheet.PaidOn }.Where(h => !pcols.ContainsKey(h)).ToList();
                if (pmissing.Count > 0)
                    throw Invalid($"Payments sheet is missing column(s): {string.Join(", ", pmissing)}.");

                var lastPay = payWs.LastRowUsed()?.RowNumber() ?? 1;
                if (lastPay - 1 > MaxRows) throw Invalid($"Too many payment rows ({lastPay - 1}). Import at most {MaxRows} at a time.");
                for (var r = 2; r <= lastPay; r++)
                {
                    var row = payWs.Row(r);
                    string Text(string col) => pcols.TryGetValue(col, out var i) ? row.Cell(i).GetFormattedString().Trim() : "";
                    IXLCell? Cell(string col) => pcols.TryGetValue(col, out var i) ? row.Cell(i) : null;
                    if (BorrowerSheet.PaymentColumns.All(h => Text(h).Length == 0)) continue;

                    var rowErrors = new List<string>();
                    var no = ImportLoansHandler.Number(Cell(BorrowerSheet.LoanNo));
                    var amount = ImportLoansHandler.Number(Cell(BorrowerSheet.Amount));
                    var paidOn = ImportLoansHandler.Date(Cell(BorrowerSheet.PaidOn));

                    Loan? loan = null;
                    if (no is null || no != Math.Floor(no.Value)) rowErrors.Add("Loan # must be a whole number");
                    else if (badLoanNos.Contains((int)no.Value)) continue;
                    else if (!loans.TryGetValue((int)no.Value, out loan)) rowErrors.Add($"Loan # {no} isn't a valid row on the Loans sheet");
                    if (amount is not > 0) rowErrors.Add("amount must be a number above 0");
                    if (paidOn is null) rowErrors.Add("paid on is missing or not a date");

                    if (rowErrors.Count > 0)
                    {
                        errors.Add($"Payments row {r}: {string.Join("; ", rowErrors)}.");
                        continue;
                    }

                    loan!.Payments.Add(new Payment
                    {
                        LoanId = loan.Id,
                        Amount = Math.Round(amount!.Value, 2),
                        PaidOn = paidOn!.Value,
                        Note = Text(BorrowerSheet.Note) is { Length: > 0 } n ? n : null,
                    });
                    payments++;
                }
            }

            // Per-loan checks once all payments are attached; same rules as recording a payment.
            foreach (var (no, loan) in loans.OrderBy(x => x.Key))
            {
                var paid = loan.Payments.Sum(p => p.Amount);
                if (paid == 0) continue;
                var totalDue = loan.Terms().TotalDue;
                if (loan.Status is LoanStatus.Pending or LoanStatus.Cancelled)
                    errors.Add($"Loan # {no}: a {LoanSheet.Label(loan.Status)} loan can't have payments.");
                else if (paid > totalDue)
                    errors.Add($"Loan # {no}: payments ({paid:N2}) add up to more than the total owed ({totalDue:N2}).");
                else if (paid >= totalDue && loan.Status is LoanStatus.Active or LoanStatus.Defaulted) loan.Status = LoanStatus.PaidOff;
                else if (paid < totalDue && loan.Status == LoanStatus.PaidOff) loan.Status = LoanStatus.Active;
            }

            if (errors.Count > 0)
                throw new ValidationException(new Dictionary<string, string[]> { ["file"] = errors.ToArray() });
            if (loans.Count == 0)
                throw Invalid("No loan rows found on the Loans sheet.");

            db.Loans.AddRange(loans.Values);
            await db.SaveChangesAsync(ct);
            return new BorrowerImportResult(loans.Count, payments);
        }
    }

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["file"] = [message] });
}

// ───────────── Endpoints ─────────────

public static class BorrowerExcelEndpoints
{
    public static void MapBorrowerExcel(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/borrowers").WithTags("Borrowers (Excel)");

        g.MapGet("/{id:guid}/export", async (Guid id, IDispatcher d, CancellationToken ct) =>
        {
            var (name, file) = await d.Query(new ExportBorrowerQuery(id), ct);
            var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            if (safe.Length == 0) safe = "borrower";
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Results.File(file, LoanSheet.ContentType, $"{safe}-{today}.xlsx");
        });

        // Session cookies are SameSite=Strict, so a cross-site form can't post here; antiforgery tokens aren't needed.
        g.MapPost("/{id:guid}/import", async (Guid id, IFormFile file, IDispatcher d, CancellationToken ct) =>
        {
            if (file.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is empty."] });
            if (file.Length > 5 * 1024 * 1024) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is larger than 5 MB."] });
            await using var stream = file.OpenReadStream();
            return Results.Ok(await d.Send(new ImportBorrowerCommand(id, stream), ct));
        }).DisableAntiforgery();
    }
}
