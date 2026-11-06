# LendingApp

A lending web app: borrowers, loans and payments with full CRUD, a CQRS API, a React UI, and an automatic change history.

| Part | Tech |
|---|---|
| API | ASP.NET Core (.NET 10) minimal APIs, CQRS, EF Core |
| Database | SQLite (`backend/LendingApp.Api/lending.db`, created on first run) |
| UI | React 19 + Vite + React Router |

## Run it

Terminal 1 — API on http://localhost:5080:

```bash
dotnet run --project backend/LendingApp.Api
```

Terminal 2 — UI on http://localhost:5173 (proxies `/api` to the API):

```bash
cd frontend && npm install && npm run dev
```

Single-server option: `cd frontend && npm run build` writes the UI into the API's `wwwroot`, then just run the API and open http://localhost:5080.

Demo data is seeded on first start (turn off with `"SeedDemoData": false` in `appsettings.json`). Delete `lending.db` to start fresh.

## How CQRS is laid out

```
backend/LendingApp.Api/
  Cqrs/            ICommand / IQuery, handler interfaces, Dispatcher (no MediatR dependency)
  Domain/          Borrower, Loan, Payment, AuditEntry
  Infrastructure/  LendingDbContext (writes history on every save), demo seed
  Features/
    Borrowers/     Create/Update/Delete commands · GetBorrowers/GetBorrower queries · endpoints
    Loans/         Create/Update/Delete commands · GetLoans/GetLoan queries · amortization math
    Payments/      Record/Update/Delete commands (keep loan status in sync with balance)
    History/       GetHistory query (filter by type, action, loan, borrower; paged)
    Dashboard/     GetDashboard query
```

- **Commands** (POST/PUT/DELETE) load tracked entities, validate business rules, and save.
- **Queries** (GET) read with `AsNoTracking` and project straight into DTOs.
- Endpoints only build a command/query and hand it to `IDispatcher`.

## History

`LendingDbContext.SaveChangesAsync` inspects the change tracker and writes an `AuditEntry` for every created, updated or deleted borrower, loan or payment — in the same transaction — with the field-level before/after values. Entries are tagged with their loan and borrower, so a loan's history includes its payments and a borrower's history includes all of their loans.

## API

| Method | Route | |
|---|---|---|
| GET | `/api/dashboard` | totals and loan counts by status |
| GET / POST | `/api/borrowers` | list (`?search=`) / create |
| GET / PUT / DELETE | `/api/borrowers/{id}` | |
| GET / POST | `/api/loans` | list (`?status=&borrowerId=&search=`) / create |
| GET / PUT / DELETE | `/api/loans/{id}` | detail includes payments |
| POST | `/api/loans/{id}/payments` | record payment |
| PUT / DELETE | `/api/payments/{id}` | |
| GET | `/api/history` | `?entityType=&action=&loanId=&borrowerId=&page=&pageSize=` |

OpenAPI document: http://localhost:5080/openapi/v1.json (Development).

## Business rules

- Borrower email must be unique; a borrower with loans can't be deleted.
- Loans use a fixed monthly installment (standard amortization).
- Payments are allowed on Active/Defaulted loans and can't exceed the balance.
- Paying the balance in full sets the loan to **PaidOff**; deleting/reducing a payment reopens it to **Active**.
- Deleting a loan deletes its payments (each recorded in history).

## Interest types

- **Flat** — the rate is charged once: total owed = principal × (1 + rate%). E.g. 2,500 at 20% flat → 3,000.
- **Amortized** — annual rate, repaid in equal monthly installments.

## Importing the Excel workbook

`tools/import-excel` imports `pautang.xlsx` (one sheet per borrower) through the API, so every record appears in history.

```bash
cd tools/import-excel && npm install
```

```bash
node import.mjs "F:/Ariel/pautang/pautang.xlsx"
```

```bash
node import.mjs "F:/Ariel/pautang/pautang.xlsx" --apply
```

The first run is a dry run that prints every loan, payment and balance; `--apply` imports (it refuses if borrowers already exist). Rules it follows: *Utang* includes 20% flat interest (principal = Utang ÷ 1.2); a *Renew* row settles the old loan's remaining balance on the renewal date; "Other utang" tables are separate loans.
