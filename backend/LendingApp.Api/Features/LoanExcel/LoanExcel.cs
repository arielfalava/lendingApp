using System.Globalization;
using ClosedXML.Excel;
using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.LoanExcel;

/// <summary>
/// Excel layout shared by the import template, the import, and the export.
/// The export adds read-only figures after these columns; the import ignores any column it doesn't know.
/// </summary>
internal static class LoanSheet
{
    public const string SheetName = "Loans";
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string Borrower = "Borrower name";
    public const string Principal = "Principal";
    public const string Interest = "Interest %";
    public const string Term = "Term";
    public const string TermUnit = "Term unit";
    public const string StartDate = "Start date";
    public const string Status = "Status";
    public const string Remarks = "Remarks";
    public const string PaidSoFar = "Amount paid so far";
    public const string PaidOn = "Paid on";

    public static readonly string[] Columns = [Borrower, Principal, Interest, Term, TermUnit, StartDate, Status, Remarks, PaidSoFar, PaidOn];
    public static readonly string[] Required = [Borrower, Principal, Interest, TermUnit, StartDate];

    public static readonly string[] TermUnitLabels = ["Days", "Weeks", "Months", "No deadline"];
    public static readonly string[] StatusLabels = ["Active", "Pending", "Paid off", "Defaulted", "Cancelled"];

    public static string Label(Domain.TermUnit u) => u == Domain.TermUnit.NoDeadline ? "No deadline" : u.ToString();
    public static string Label(LoanStatus s) => s == LoanStatus.PaidOff ? "Paid off" : s.ToString();

    public static Domain.TermUnit? ParseTermUnit(string s) => Normalize(s) switch
    {
        "day" or "days" or "daily" => Domain.TermUnit.Days,
        "week" or "weeks" or "weekly" => Domain.TermUnit.Weeks,
        "month" or "months" or "monthly" => Domain.TermUnit.Months,
        "nodeadline" or "nospecificdeadline" or "none" or "open" => Domain.TermUnit.NoDeadline,
        _ => null
    };

    public static LoanStatus? ParseStatus(string s) => Normalize(s) switch
    {
        "" or "active" => LoanStatus.Active,
        "pending" => LoanStatus.Pending,
        "paidoff" or "paid" => LoanStatus.PaidOff,
        "defaulted" => LoanStatus.Defaulted,
        "cancelled" or "canceled" => LoanStatus.Cancelled,
        _ => null
    };

    private static string Normalize(string s) => new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();

    /// <summary>Writes the header row with the template's styling and dropdowns for the first <paramref name="rows"/> rows.</summary>
    public static IXLWorksheet AddLoansSheet(XLWorkbook wb, IEnumerable<string> headers, int rows)
    {
        var ws = wb.AddWorksheet(SheetName);
        var cols = headers.ToList();
        for (var c = 0; c < cols.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = cols[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = Required.Contains(cols[c]) ? XLColor.FromHtml("#DCE8FB") : XLColor.FromHtml("#EEF0F3");
        }
        ws.SheetView.FreezeRows(1);

        var lastRow = Math.Max(rows, 1) + 1;
        ws.Range(2, Index(TermUnit), lastRow, Index(TermUnit)).CreateDataValidation().List($"\"{string.Join(",", TermUnitLabels)}\"");
        ws.Range(2, Index(Status), lastRow, Index(Status)).CreateDataValidation().List($"\"{string.Join(",", StatusLabels)}\"");
        ws.Range(2, Index(StartDate), lastRow, Index(StartDate)).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(2, Index(PaidOn), lastRow, Index(PaidOn)).Style.DateFormat.Format = "yyyy-mm-dd";
        foreach (var money in new[] { Principal, PaidSoFar })
            ws.Range(2, Index(money), lastRow, Index(money)).Style.NumberFormat.Format = "#,##0.00";
        return ws;
    }

    public static int Index(string column) => Array.IndexOf(Columns, column) + 1;

    public static byte[] ToBytes(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

// ───────────── Template (query) ─────────────

public record GetImportTemplateQuery : IQuery<byte[]>;

public class GetImportTemplateHandler : IQueryHandler<GetImportTemplateQuery, byte[]>
{
    public Task<byte[]> Handle(GetImportTemplateQuery q, CancellationToken ct)
    {
        using var wb = new XLWorkbook();
        var ws = LoanSheet.AddLoansSheet(wb, LoanSheet.Columns, 500);
        ws.Columns().Width = 18;
        ws.Column(LoanSheet.Index(LoanSheet.Remarks)).Width = 36;

        var help = wb.AddWorksheet("How to fill");
        string[][] lines =
        [
            ["Fill one loan per row on the \"Loans\" sheet. Blue columns are required.", ""],
            ["", ""],
            ["Column", "What to enter"],
            [LoanSheet.Borrower, "Borrower's name. An existing borrower with exactly this name is used; otherwise a new borrower is created."],
            [LoanSheet.Principal, "Amount lent, e.g. 2500"],
            [LoanSheet.Interest, "Interest rate in percent, charged once on the principal. 20 means 2,500 → 3,000 owed."],
            [LoanSheet.Term, "Number of days, weeks or months. Leave blank when Term unit is \"No deadline\"."],
            [LoanSheet.TermUnit, string.Join(", ", LoanSheet.TermUnitLabels)],
            [LoanSheet.StartDate, "Date the loan was released, e.g. 2026-11-07"],
            [LoanSheet.Status, $"Optional, default Active. One of: {string.Join(", ", LoanSheet.StatusLabels)}"],
            [LoanSheet.Remarks, "Optional notes"],
            [LoanSheet.PaidSoFar, "Optional. Total already repaid; recorded as one payment."],
            [LoanSheet.PaidOn, "Optional date of that payment. Defaults to the start date."],
            ["", ""],
            ["Example row", ""],
        ];
        for (var r = 0; r < lines.Length; r++)
        {
            help.Cell(r + 1, 1).Value = lines[r][0];
            help.Cell(r + 1, 2).Value = lines[r][1];
        }
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Row(3).Style.Font.Bold = true;
        help.Row(15).Style.Font.Bold = true;
        object[] example = ["Juan Dela Cruz", 2500, 20, 8, "Weeks", new DateTime(2026, 11, 7), "Active", "Weekly every Saturday", 750, new DateTime(2026, 11, 21)];
        for (var c = 0; c < LoanSheet.Columns.Length; c++)
        {
            help.Cell(16, c + 1).Value = LoanSheet.Columns[c];
            help.Cell(16, c + 1).Style.Font.Bold = true;
            help.Cell(17, c + 1).Value = XLCellValue.FromObject(example[c]);
        }
        help.Range(17, 6, 17, 6).Style.DateFormat.Format = "yyyy-mm-dd";
        help.Range(17, 10, 17, 10).Style.DateFormat.Format = "yyyy-mm-dd";
        help.Column(1).Width = 22;
        help.Column(2).Width = 90;

        return Task.FromResult(LoanSheet.ToBytes(wb));
    }
}

// ───────────── Export (query) ─────────────

public record ExportLoansQuery(LoanStatus? Status, Guid? BorrowerId, string? Search) : IQuery<byte[]>;

public class ExportLoansHandler(IDispatcher dispatcher, LendingDbContext db) : IQueryHandler<ExportLoansQuery, byte[]>
{
    public async Task<byte[]> Handle(ExportLoansQuery q, CancellationToken ct)
    {
        var loans = await dispatcher.Query(new GetLoansQuery(q.Status, q.BorrowerId, q.Search), ct);
        var lastPaid = await db.Payments.AsNoTracking()
            .GroupBy(p => p.LoanId)
            .Select(g => new { g.Key, Last = g.Max(p => p.PaidOn) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, ct);

        string[] extra = ["Total owed", "Remaining balance"];
        using var wb = new XLWorkbook();
        var ws = LoanSheet.AddLoansSheet(wb, LoanSheet.Columns.Concat(extra), loans.Count);

        var r = 2;
        foreach (var l in loans)
        {
            ws.Cell(r, 1).Value = l.BorrowerName;
            ws.Cell(r, 2).Value = l.Principal;
            ws.Cell(r, 3).Value = l.InterestRate;
            if (l.TermUnit != TermUnit.NoDeadline) ws.Cell(r, 4).Value = l.Term;
            ws.Cell(r, 5).Value = LoanSheet.Label(l.TermUnit);
            ws.Cell(r, 6).Value = l.StartDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 7).Value = LoanSheet.Label(l.Status);
            ws.Cell(r, 8).Value = l.Notes;
            ws.Cell(r, 9).Value = l.TotalPaid;
            if (lastPaid.TryGetValue(l.Id, out var paidOn)) ws.Cell(r, 10).Value = paidOn.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 11).Value = l.TotalDue;
            ws.Cell(r, 12).Value = l.Balance;
            r++;
        }

        ws.Range(2, 11, Math.Max(r - 1, 2), 12).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(1, 11).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4D6");
        ws.Cell(1, 12).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4D6");
        if (loans.Count > 0)
        {
            ws.Cell(r, 11).Value = "Total";
            ws.Cell(r, 11).Style.Font.Bold = true;
            ws.Cell(r, 12).FormulaA1 = $"SUM(L2:L{r - 1})";
            ws.Cell(r, 12).Style.Font.Bold = true;
            ws.Cell(r, 12).Style.NumberFormat.Format = "#,##0.00";
        }
        ws.Columns().AdjustToContents(1, Math.Min(r, 300));
        ws.Column(8).Width = Math.Min(ws.Column(8).Width, 50);

        return LoanSheet.ToBytes(wb);
    }
}

// ───────────── Import (command) ─────────────

public record ImportResult(int LoansCreated, int BorrowersCreated, int PaymentsCreated);

/// <summary>Imports every row or none: if any row is invalid, nothing is saved and all row errors are returned.</summary>
public record ImportLoansCommand(Stream File) : ICommand<ImportResult>;

public class ImportLoansHandler(LendingDbContext db) : ICommandHandler<ImportLoansCommand, ImportResult>
{
    private const int MaxRows = 2000;

    public async Task<ImportResult> Handle(ImportLoansCommand c, CancellationToken ct)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(c.File); }
        catch (Exception) { throw Invalid("That file isn't a readable Excel (.xlsx) workbook."); }

        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(LoanSheet.SheetName, StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.First();

            // Map headers by name so column order doesn't matter. A trailing "*" or extra spaces are ignored.
            var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(1).CellsUsed())
                cols.TryAdd(cell.GetString().Trim().TrimEnd('*').Trim(), cell.Address.ColumnNumber);
            var missing = LoanSheet.Required.Where(h => !cols.ContainsKey(h)).ToList();
            if (missing.Count > 0)
                throw Invalid($"Missing column(s): {string.Join(", ", missing)}. Download the template to see the expected layout.");

            var borrowers = await db.Borrowers.ToListAsync(ct);
            var byName = borrowers.GroupBy(b => FullName(b), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var errors = new List<string>();
            int loans = 0, newBorrowers = 0, payments = 0;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxRows) throw Invalid($"Too many rows ({lastRow - 1}). Import at most {MaxRows} at a time.");

            for (var r = 2; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                string Text(string col) => cols.TryGetValue(col, out var i) ? row.Cell(i).GetFormattedString().Trim() : "";
                IXLCell? Cell(string col) => cols.TryGetValue(col, out var i) ? row.Cell(i) : null;

                if (LoanSheet.Columns.All(h => Text(h).Length == 0)) continue; // blank row

                var rowErrors = new List<string>();
                var name = Text(LoanSheet.Borrower);
                var principal = Number(Cell(LoanSheet.Principal));
                var rate = Number(Cell(LoanSheet.Interest));
                var unit = LoanSheet.ParseTermUnit(Text(LoanSheet.TermUnit));
                var termText = Text(LoanSheet.Term);
                var term = Number(Cell(LoanSheet.Term));
                var start = Date(Cell(LoanSheet.StartDate));
                var status = LoanSheet.ParseStatus(Text(LoanSheet.Status));
                var paidText = Text(LoanSheet.PaidSoFar);
                var paid = Number(Cell(LoanSheet.PaidSoFar));
                var paidOnText = Text(LoanSheet.PaidOn);
                var paidOn = Date(Cell(LoanSheet.PaidOn));

                if (name.Length == 0) rowErrors.Add("borrower name is empty");
                if (principal is not > 0) rowErrors.Add("principal must be a number above 0");
                if (rate is not (>= 0 and <= 100)) rowErrors.Add("interest % must be a number from 0 to 100");
                if (unit is null) rowErrors.Add($"term unit must be one of {string.Join(", ", LoanSheet.TermUnitLabels)}");
                else if (unit != TermUnit.NoDeadline && (term is not > 0 || term != Math.Floor(term.Value)))
                    rowErrors.Add($"term must be a whole number of {unit.ToString()!.ToLowerInvariant()}");
                if (start is null) rowErrors.Add("start date is missing or not a date");
                if (status is null) rowErrors.Add($"status must be one of {string.Join(", ", LoanSheet.StatusLabels)}");
                if (paidText.Length > 0 && paid is not >= 0) rowErrors.Add("amount paid so far must be a number");
                if (paidOnText.Length > 0 && paidOn is null) rowErrors.Add("paid on is not a date");

                Borrower? borrower = null;
                if (name.Length > 0 && byName.TryGetValue(name, out var matches))
                {
                    if (matches.Count > 1) rowErrors.Add($"{matches.Count} borrowers are named \"{name}\"; rename one so the import knows which");
                    else borrower = matches[0];
                }

                Loan? loan = null;
                if (rowErrors.Count == 0)
                {
                    loan = new Loan
                    {
                        Principal = Math.Round(principal!.Value, 2),
                        InterestRate = rate!.Value,
                        InterestType = InterestType.Flat,
                        Term = unit == TermUnit.NoDeadline ? 0 : (int)term!.Value,
                        TermUnit = unit!.Value,
                        StartDate = start!.Value,
                        Status = status!.Value,
                        Notes = Text(LoanSheet.Remarks) is { Length: > 0 } n ? n : null,
                    };
                    var totalDue = loan.Terms().TotalDue;
                    if (paid > totalDue)
                        rowErrors.Add($"amount paid so far ({paid:N2}) is more than the total owed ({totalDue:N2})");
                    if (paid > 0 && loan.Status is LoanStatus.Pending or LoanStatus.Cancelled)
                        rowErrors.Add($"a {LoanSheet.Label(loan.Status)} loan can't have payments");
                }

                if (rowErrors.Count > 0)
                {
                    errors.Add($"Row {r}: {string.Join("; ", rowErrors)}.");
                    continue;
                }

                if (borrower is null)
                {
                    borrower = new Borrower { FirstName = name };
                    db.Borrowers.Add(borrower);
                    byName[name] = [borrower];
                    newBorrowers++;
                }

                loan!.BorrowerId = borrower.Id;
                db.Loans.Add(loan);
                loans++;

                if (paid > 0)
                {
                    loan.Payments.Add(new Payment
                    {
                        LoanId = loan.Id,
                        Amount = Math.Round(paid.Value, 2),
                        PaidOn = paidOn ?? loan.StartDate,
                        Note = "Paid so far (Excel import)",
                    });
                    payments++;
                    // Same rule as recording a payment: fully paid ⇒ Paid off, otherwise reopen a "Paid off" row.
                    var balance = loan.Terms().TotalDue - paid.Value;
                    if (balance <= 0 && loan.Status is LoanStatus.Active or LoanStatus.Defaulted) loan.Status = LoanStatus.PaidOff;
                    else if (balance > 0 && loan.Status == LoanStatus.PaidOff) loan.Status = LoanStatus.Active;
                }
            }

            if (errors.Count > 0)
                throw new ValidationException(new Dictionary<string, string[]> { ["file"] = errors.ToArray() });
            if (loans == 0)
                throw Invalid("No loan rows found under the header row.");

            await db.SaveChangesAsync(ct);
            return new ImportResult(loans, newBorrowers, payments);
        }
    }

    private static string FullName(Borrower b) => $"{b.FirstName} {b.LastName}".Trim();

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["file"] = [message] });

    private static decimal? Number(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        if (cell.Value.IsNumber) return (decimal)cell.Value.GetNumber();
        var s = cell.GetString().Replace(",", "").Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d MMM yyyy", "MMM d, yyyy", "yyyy/M/d"];

    private static DateOnly? Date(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        if (cell.Value.IsDateTime) return DateOnly.FromDateTime(cell.Value.GetDateTime());
        if (cell.Value.IsNumber) // a date typed into a cell formatted as a number
        {
            try { return DateOnly.FromDateTime(DateTime.FromOADate(cell.Value.GetNumber())); } catch { return null; }
        }
        return DateOnly.TryParseExact(cell.GetString().Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}

// ───────────── Endpoints ─────────────

public static class LoanExcelEndpoints
{
    public static void MapLoanExcel(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/loans").WithTags("Loans (Excel)");
        var today = () => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        g.MapGet("/export", async (LoanStatus? status, Guid? borrowerId, string? search, IDispatcher d, CancellationToken ct) =>
            Results.File(await d.Query(new ExportLoansQuery(status, borrowerId, search), ct), LoanSheet.ContentType, $"loans-{today()}.xlsx"));

        g.MapGet("/import-template", async (IDispatcher d, CancellationToken ct) =>
            Results.File(await d.Query(new GetImportTemplateQuery(), ct), LoanSheet.ContentType, "loan-import-template.xlsx"));

        // Session cookies are SameSite=Strict, so a cross-site form can't post here; antiforgery tokens aren't needed.
        g.MapPost("/import", async (IFormFile file, IDispatcher d, CancellationToken ct) =>
        {
            if (file.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is empty."] });
            if (file.Length > 5 * 1024 * 1024) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is larger than 5 MB."] });
            await using var stream = file.OpenReadStream();
            return Results.Ok(await d.Send(new ImportLoansCommand(stream), ct));
        }).DisableAntiforgery();
    }
}
