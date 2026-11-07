using System.Globalization;
using ClosedXML.Excel;
using LendingApp.Api.Cqrs;
using LendingApp.Api.Domain;
using LendingApp.Api.Features.Loans;
using LendingApp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LendingApp.Api.Features.LoanExcel;

/// <summary>
/// Excel layout shared by the import template, the import, and the export: one sheet per borrower, named after
/// the borrower, one loan per row. The export adds a Summary sheet and read-only figures after these columns;
/// the import skips sheets without a Principal column and ignores any column it doesn't know.
/// A sheet with a "Borrower name" column (the older single-sheet layout) takes the borrower from each row instead.
/// </summary>
internal static class LoanSheet
{
    public const string SheetName = "Loans";
    public const string SummarySheet = "Summary";
    public const string HelpSheet = "How to fill";
    /// <summary>The template's blank borrower sheet, before the user renames it.</summary>
    public const string TemplateSheet = "Borrower name here";
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

    /// <summary>Columns of a borrower sheet (the borrower is the sheet name).</summary>
    public static readonly string[] Columns = [Principal, Interest, Term, TermUnit, StartDate, Status, Remarks, PaidSoFar, PaidOn];
    public static readonly string[] Required = [Principal, Interest, TermUnit, StartDate];

    private static readonly char[] SheetNameInvalid = ['[', ']', ':', '*', '?', '/', '\\'];

    /// <summary>
    /// The borrower's name as Excel allows it for a sheet: at most 31 characters, none of []:*?/\, not "History"
    /// and not one of this workbook's own sheet names. The import matches sheets back to borrowers through this too.
    /// </summary>
    public static string SheetNameFor(string borrowerName)
    {
        var s = new string(borrowerName.Select(ch => SheetNameInvalid.Contains(ch) ? '-' : ch).ToArray()).Trim().Trim('\'').Trim();
        if (s.Length > 31) s = s[..31].TrimEnd();
        if (s.Length == 0) s = "Borrower";
        if (s.Equals("History", StringComparison.OrdinalIgnoreCase) || IsReserved(s)) s = $"{s} (borrower)";
        return s;
    }

    public static bool IsReserved(string sheetName) =>
        sheetName.Equals(SummarySheet, StringComparison.OrdinalIgnoreCase) || sheetName.Equals(HelpSheet, StringComparison.OrdinalIgnoreCase);

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
    public static IXLWorksheet AddLoansSheet(XLWorkbook wb, string sheetName, IEnumerable<string> headers, int rows)
    {
        var ws = wb.AddWorksheet(sheetName);
        var cols = headers.ToList();
        for (var c = 0; c < cols.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = cols[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = Required.Contains(cols[c]) ? XLColor.FromHtml("#DCE8FB") : XLColor.FromHtml("#EEF0F3");
        }
        ws.SheetView.FreezeRows(1);

        // Styles go by column name, so a sheet that leaves some columns out (the export has no Remarks) still lines up.
        var lastRow = Math.Max(rows, 1) + 1;
        IXLRange? Col(string name) => cols.IndexOf(name) is >= 0 and var i ? ws.Range(2, i + 1, lastRow, i + 1) : null;
        Col(TermUnit)?.CreateDataValidation().List($"\"{string.Join(",", TermUnitLabels)}\"");
        Col(Status)?.CreateDataValidation().List($"\"{string.Join(",", StatusLabels)}\"");
        foreach (var date in new[] { StartDate, PaidOn })
            if (Col(date) is { } range) range.Style.DateFormat.Format = "yyyy-mm-dd";
        foreach (var money in new[] { Principal, PaidSoFar })
            if (Col(money) is { } range) range.Style.NumberFormat.Format = "#,##0.00";
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
        var ws = LoanSheet.AddLoansSheet(wb, LoanSheet.TemplateSheet, LoanSheet.Columns, 500);
        ws.Columns().Width = 18;
        ws.Column(LoanSheet.Index(LoanSheet.Remarks)).Width = 36;
        var help = wb.AddWorksheet(LoanSheet.HelpSheet);

        string[][] lines =
        [
            ["One sheet per borrower: name each sheet after the borrower, and fill one loan per row. Blue columns are required.", ""],
            ["Right-click the sheet tab → Rename to set the borrower; right-click → Move or Copy → Create a copy to add another borrower.", ""],
            ["Column", "What to enter"],
            ["Sheet name", "Borrower's name. An existing borrower with exactly this name is used; otherwise a new borrower is created. Excel allows 31 characters and no [ ] : * ? / \\ in a sheet name."],
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
            ["Example row (on a sheet named \"Juan Dela Cruz\")", ""],
        ];
        for (var r = 0; r < lines.Length; r++)
        {
            help.Cell(r + 1, 1).Value = lines[r][0];
            help.Cell(r + 1, 2).Value = lines[r][1];
        }
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Row(3).Style.Font.Bold = true;
        help.Row(15).Style.Font.Bold = true;
        object[] example = [2500, 20, 8, "Weeks", new DateTime(2026, 11, 7), "Active", "Weekly every Saturday", 750, new DateTime(2026, 11, 21)];
        for (var c = 0; c < LoanSheet.Columns.Length; c++)
        {
            help.Cell(16, c + 1).Value = LoanSheet.Columns[c];
            help.Cell(16, c + 1).Style.Font.Bold = true;
            help.Cell(17, c + 1).Value = XLCellValue.FromObject(example[c]);
        }
        help.Cell(17, LoanSheet.Index(LoanSheet.StartDate)).Style.DateFormat.Format = "yyyy-mm-dd";
        help.Cell(17, LoanSheet.Index(LoanSheet.PaidOn)).Style.DateFormat.Format = "yyyy-mm-dd";
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

        using var wb = new XLWorkbook();
        var summary = wb.AddWorksheet(LoanSheet.SummarySheet);

        // One sheet per borrower (alphabetical), newest loan first. Columns: A Principal, B Interest %, C Term,
        // D Term unit, E Start date, F Status, G Amount paid so far, H Paid on, I Total owed, J Remaining balance.
        string[] extra = ["Total owed", "Remaining balance"];
        var headers = LoanSheet.Columns.Where(c => c != LoanSheet.Remarks).Concat(extra).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LoanSheet.SummarySheet };
        var groups = loans.GroupBy(l => l.BorrowerId)
            .Select(g => (Name: g.First().BorrowerName, Loans: g.OrderByDescending(l => l.StartDate).ThenByDescending(l => l.CreatedAt).ToList()))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var summaryRows = new List<(string Name, string Sheet, int Count, decimal Owed, decimal Paid, decimal Remaining)>();

        foreach (var (name, rows) in groups)
        {
            // Two borrowers can share a name; Excel needs unique sheet names, so the second gets " (2)".
            var baseName = LoanSheet.SheetNameFor(name);
            var sheet = baseName;
            for (var n = 2; !used.Add(sheet); n++)
            {
                var suffix = $" ({n})";
                sheet = (baseName.Length + suffix.Length > 31 ? baseName[..(31 - suffix.Length)] : baseName) + suffix;
            }

            var ws = LoanSheet.AddLoansSheet(wb, sheet, headers, rows.Count);
            var r = 2;
            foreach (var l in rows)
            {
                ws.Cell(r, 1).Value = l.Principal;
                ws.Cell(r, 2).Value = l.InterestRate;
                if (l.TermUnit != TermUnit.NoDeadline) ws.Cell(r, 3).Value = l.Term;
                ws.Cell(r, 4).Value = LoanSheet.Label(l.TermUnit);
                ws.Cell(r, 5).Value = l.StartDate.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 6).Value = LoanSheet.Label(l.Status);
                ws.Cell(r, 7).Value = l.TotalPaid;
                if (lastPaid.TryGetValue(l.Id, out var paidOn)) ws.Cell(r, 8).Value = paidOn.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 9).Value = l.TotalDue;
                ws.Cell(r, 10).Value = l.Balance;
                r++;
            }
            ws.Range(2, 9, Math.Max(r, 2), 10).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(1, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4D6");
            ws.Cell(1, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4D6");
            // Same rule as the Loans page total: only Active and Defaulted loans still count as owed
            // (a Paid off, Pending or Cancelled row can show a balance but isn't money owed).
            ws.Cell(r, 9).Value = "Total owed (Active + Defaulted)";
            ws.Cell(r, 9).Style.Font.Bold = true;
            ws.Cell(r, 10).FormulaA1 = $"SUMIFS(J2:J{r - 1},F2:F{r - 1},\"Active\")+SUMIFS(J2:J{r - 1},F2:F{r - 1},\"Defaulted\")";
            ws.Cell(r, 10).Style.Font.Bold = true;
            ws.Columns().AdjustToContents(1, Math.Min(r, 300));

            summaryRows.Add((name, sheet, rows.Count, rows.Sum(l => l.TotalDue), rows.Sum(l => l.TotalPaid),
                rows.Where(l => LoanMath.CountsAsOwed(l.Status)).Sum(l => l.Balance)));
        }

        // Summary: one line per borrower linking to their sheet, plus the grand total (matches the Loans page).
        string[] sumHead = ["Borrower", "Loans", "Total owed", "Total paid", "Remaining balance (Active + Defaulted)"];
        for (var c = 0; c < sumHead.Length; c++)
        {
            summary.Cell(1, c + 1).Value = sumHead[c];
            summary.Cell(1, c + 1).Style.Font.Bold = true;
            summary.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF0F3");
        }
        summary.SheetView.FreezeRows(1);
        var sr = 2;
        foreach (var s in summaryRows)
        {
            summary.Cell(sr, 1).Value = s.Name;
            summary.Cell(sr, 1).SetHyperlink(new XLHyperlink($"'{s.Sheet.Replace("'", "''")}'!A1"));
            summary.Cell(sr, 2).Value = s.Count;
            summary.Cell(sr, 3).Value = s.Owed;
            summary.Cell(sr, 4).Value = s.Paid;
            summary.Cell(sr, 5).Value = s.Remaining;
            sr++;
        }
        summary.Cell(sr, 1).Value = summaryRows.Count == 0 ? "No loans" : "Total";
        summary.Cell(sr, 2).Value = summaryRows.Sum(s => s.Count);
        summary.Cell(sr, 3).Value = summaryRows.Sum(s => s.Owed);
        summary.Cell(sr, 4).Value = summaryRows.Sum(s => s.Paid);
        summary.Cell(sr, 5).Value = summaryRows.Sum(s => s.Remaining);
        summary.Row(sr).Style.Font.Bold = true;
        summary.Range(2, 3, sr, 5).Style.NumberFormat.Format = "#,##0.00";
        summary.Columns().AdjustToContents();

        return LoanSheet.ToBytes(wb);
    }
}

// ───────────── Single-loan export (query) ─────────────

/// <summary>One loan as a workbook: a "Loan" sheet with its terms and totals and a "Payments" sheet with a running balance.</summary>
public record ExportLoanQuery(Guid LoanId) : IQuery<(string FileName, byte[] File)>;

public class ExportLoanHandler(IDispatcher dispatcher) : IQueryHandler<ExportLoanQuery, (string FileName, byte[] File)>
{
    public async Task<(string FileName, byte[] File)> Handle(ExportLoanQuery q, CancellationToken ct)
    {
        var detail = await dispatcher.Query(new GetLoanQuery(q.LoanId), ct);
        var l = detail.Loan;
        var payments = detail.Payments.OrderBy(p => p.PaidOn).ThenBy(p => p.CreatedAt).ToList();

        using var wb = new XLWorkbook();
        var info = wb.AddWorksheet("Loan");
        DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
        (string Label, object? Value, string? Format)[] rows =
        [
            ("Borrower", l.BorrowerName, null),
            (LoanSheet.Principal, l.Principal, "#,##0.00"),
            (LoanSheet.Interest, l.InterestRate, null),
            ("Interest type", l.InterestType.ToString(), null),
            (LoanSheet.Term, l.TermUnit == TermUnit.NoDeadline ? null : l.Term, null),
            (LoanSheet.TermUnit, LoanSheet.Label(l.TermUnit), null),
            (LoanSheet.StartDate, D(l.StartDate), "yyyy-mm-dd"),
            ("Due date", l.MaturityDate is { } m ? D(m) : null, "yyyy-mm-dd"),
            (LoanSheet.Status, LoanSheet.Label(l.Status), null),
            ("Total owed", l.TotalDue, "#,##0.00"),
            ("Total paid", l.TotalPaid, "#,##0.00"),
            ("Remaining balance", l.Balance, "#,##0.00"),
            ("Payments", payments.Count, null),
            ("Exported", DateTime.Now, "yyyy-mm-dd hh:mm"),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            info.Cell(i + 1, 1).Value = rows[i].Label;
            info.Cell(i + 1, 1).Style.Font.Bold = true;
            info.Cell(i + 1, 2).Value = XLCellValue.FromObject(rows[i].Value);
            if (rows[i].Format is { } f)
            {
                if (rows[i].Value is DateTime) info.Cell(i + 1, 2).Style.DateFormat.Format = f;
                else info.Cell(i + 1, 2).Style.NumberFormat.Format = f;
            }
        }
        info.Column(1).Width = 20;
        info.Column(2).Width = 50;

        var ps = wb.AddWorksheet("Payments");
        string[] head = ["Paid on", "Amount", "Balance after", "Note"];
        for (var c = 0; c < head.Length; c++)
        {
            ps.Cell(1, c + 1).Value = head[c];
            ps.Cell(1, c + 1).Style.Font.Bold = true;
            ps.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF0F3");
        }
        ps.SheetView.FreezeRows(1);
        var r = 2;
        var balance = l.TotalDue;
        foreach (var p in payments)
        {
            balance -= p.Amount;
            ps.Cell(r, 1).Value = D(p.PaidOn);
            ps.Cell(r, 2).Value = p.Amount;
            ps.Cell(r, 3).Value = balance;
            ps.Cell(r, 4).Value = p.Note;
            r++;
        }
        if (payments.Count > 0)
        {
            ps.Cell(r, 1).Value = "Total paid";
            ps.Cell(r, 1).Style.Font.Bold = true;
            ps.Cell(r, 2).FormulaA1 = $"SUM(B2:B{r - 1})";
            ps.Cell(r, 2).Style.Font.Bold = true;
        }
        ps.Range(2, 1, Math.Max(r, 2), 1).Style.DateFormat.Format = "yyyy-mm-dd";
        ps.Range(2, 2, Math.Max(r, 2), 3).Style.NumberFormat.Format = "#,##0.00";
        ps.Column(1).Width = 14;
        ps.Column(2).Width = 14;
        ps.Column(3).Width = 14;
        ps.Column(4).Width = 40;

        var who = new string(l.BorrowerName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        var name = $"loan-{(who.Length > 0 ? who + "-" : "")}{l.StartDate:yyyy-MM-dd}.xlsx";
        return (name, LoanSheet.ToBytes(wb));
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
            var borrowers = await db.Borrowers.ToListAsync(ct);
            var byName = borrowers.GroupBy(b => FullName(b), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            // An exported sheet name can be a shortened/cleaned version of the borrower's name (Excel's sheet-name
            // rules), so also match on that. Exact names win.
            var bySheetName = borrowers.GroupBy(b => LoanSheet.SheetNameFor(FullName(b)), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var errors = new List<string>();
            int loans = 0, newBorrowers = 0, payments = 0, totalRows = 0, loanSheets = 0;

            // Every sheet with a Principal column is a loan sheet; Summary, "How to fill" and anything else are skipped.
            foreach (var ws in wb.Worksheets)
            {
                // Map headers by name so column order doesn't matter. A trailing "*" or extra spaces are ignored.
                var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in ws.Row(1).CellsUsed())
                    cols.TryAdd(cell.GetString().Trim().TrimEnd('*').Trim(), cell.Address.ColumnNumber);
                if (!cols.ContainsKey(LoanSheet.Principal) || LoanSheet.IsReserved(ws.Name)) continue;
                loanSheets++;

                if (cols.ContainsKey("Loan #"))
                {
                    errors.Add($"Sheet \"{ws.Name}\" is from a borrower's Export. Import that file on the borrower's page instead.");
                    continue;
                }
                if (!cols.ContainsKey(LoanSheet.Borrower) && ws.Name.Trim().Equals(LoanSheet.TemplateSheet, StringComparison.OrdinalIgnoreCase))
                {
                    if (ws.RowsUsed().Skip(1).Any())
                        errors.Add($"Sheet \"{ws.Name}\": rename the sheet to the borrower's name.");
                    continue;
                }

                var missing = LoanSheet.Required.Where(h => !cols.ContainsKey(h)).ToList();
                if (missing.Count > 0)
                {
                    errors.Add($"Sheet \"{ws.Name}\": missing column(s) {string.Join(", ", missing)}. Download the template to see the expected layout.");
                    continue;
                }

                // Older single-sheet files name the borrower on every row; otherwise the sheet name is the borrower.
                var perRowBorrower = cols.ContainsKey(LoanSheet.Borrower);
                var sheetBorrower = ws.Name.Trim();

                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                totalRows += Math.Max(lastRow - 1, 0);
                if (totalRows > MaxRows) throw Invalid($"Too many rows ({totalRows}+). Import at most {MaxRows} at a time.");

                for (var r = 2; r <= lastRow; r++)
                {
                    var row = ws.Row(r);
                    string Text(string col) => cols.TryGetValue(col, out var i) ? row.Cell(i).GetFormattedString().Trim() : "";
                    IXLCell? Cell(string col) => cols.TryGetValue(col, out var i) ? row.Cell(i) : null;

                    // Blank rows, and the export's total row (it only fills the read-only columns), are skipped.
                    if (LoanSheet.Columns.Append(LoanSheet.Borrower).All(h => Text(h).Length == 0)) continue;

                    var rowErrors = new List<string>();
                    var name = perRowBorrower ? Text(LoanSheet.Borrower) : sheetBorrower;
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
                    if (name.Length > 0 && (byName.TryGetValue(name, out var matches) || (!perRowBorrower && bySheetName.TryGetValue(name, out matches))))
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
                        errors.Add($"Sheet \"{ws.Name}\" row {r}: {string.Join("; ", rowErrors)}.");
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
            }

            if (errors.Count > 0)
                throw new ValidationException(new Dictionary<string, string[]> { ["file"] = errors.ToArray() });
            if (loanSheets == 0)
                throw Invalid("No borrower sheets found. Each borrower needs a sheet named after them with a Principal column — download the template to see the layout.");
            if (loans == 0)
                throw Invalid("No loan rows found under the header rows.");

            await db.SaveChangesAsync(ct);
            return new ImportResult(loans, newBorrowers, payments);
        }
    }

    private static string FullName(Borrower b) => $"{b.FirstName} {b.LastName}".Trim();

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["file"] = [message] });

    internal static decimal? Number(IXLCell? cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        if (cell.Value.IsNumber) return (decimal)cell.Value.GetNumber();
        var s = cell.GetString().Replace(",", "").Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d MMM yyyy", "MMM d, yyyy", "yyyy/M/d"];

    internal static DateOnly? Date(IXLCell? cell)
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

        g.MapGet("/{id:guid}/export", async (Guid id, IDispatcher d, CancellationToken ct) =>
        {
            var (name, file) = await d.Query(new ExportLoanQuery(id), ct);
            return Results.File(file, LoanSheet.ContentType, name);
        });

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
