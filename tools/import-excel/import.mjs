// Imports the "pautang" workbook into LendingApp through its public API (so every record lands in history).
//
//   node import.mjs [path/to/pautang.xlsx] [--apply] [--api http://localhost:5080]
//
// Without --apply it only prints what it would import.
//
// Workbook layout (one sheet per borrower):
//   Date | Hulog (payment) | Utang (amount owed before this row) | Balance | Status ...
//   - First row of a block: Hulog 0/blank, Utang = amount owed  -> new loan
//   - Following rows with Hulog > 0                              -> payments
//   - A "Renew" row closes the current loan and starts the next block
//   - Side tables headed "Other utang" / "Other" are separate loans
//
// Decisions agreed with the owner:
//   - Utang already includes 20% flat interest -> principal = Utang / 1.2, rate 20% Flat
//   - On renewal, the old loan's remaining balance was deducted from the new release
//     -> recorded as a closing payment on the renewal date ("settled on renewal")

import ExcelJS from 'exceljs'

const args = process.argv.slice(2)
const FILE = args.find((a) => !a.startsWith('--') && /\.xlsx$/i.test(a)) ?? 'F:/Ariel/pautang/pautang.xlsx'
const APPLY = args.includes('--apply')
const API = args.includes('--api') ? args[args.indexOf('--api') + 1] : 'http://localhost:5080'

const FLAT_RATE = 20
const DEFAULT_TERM_MONTHS = 2
const SOURCE = 'pautang.xlsx'

// ───────────── cell helpers ─────────────

const raw = (ws, r, c) => {
  let v = ws.getRow(r).getCell(c).value
  if (v && typeof v === 'object' && !(v instanceof Date)) {
    if ('result' in v) v = v.result
    else if ('richText' in v) v = v.richText.map((t) => t.text).join('')
    else if ('formula' in v || 'sharedFormula' in v) v = undefined
  }
  return v ?? null
}

const num = (ws, r, c) => {
  const v = raw(ws, r, c)
  if (typeof v === 'number') return v
  // Some cells hold numbers typed as text, e.g. "12000" or "3,600".
  if (typeof v === 'string' && /^-?[\d,]+(\.\d+)?$/.test(v.trim())) return Number(v.replace(/,/g, ''))
  return null
}

const text = (ws, r, c) => {
  const v = raw(ws, r, c)
  return typeof v === 'string' ? v.trim() : null
}

/** Excel dates come back as Date (UTC) or, when typed oddly, as text like "8/24/2026" or "10//31/2026". */
const date = (ws, r, c) => {
  const v = raw(ws, r, c)
  if (v instanceof Date) return v.toISOString().slice(0, 10)
  if (typeof v === 'string') {
    const m = v.match(/^(\d{1,2})\/+(\d{1,2})\/+(\d{4})$/)
    if (m) return `${m[3]}-${m[1].padStart(2, '0')}-${m[2].padStart(2, '0')}`
  }
  return null
}

const round2 = (x) => Math.round(x * 100) / 100
const monthsBetween = (a, b) => (new Date(b) - new Date(a)) / (1000 * 60 * 60 * 24 * 30.4)
const IGNORED_STATUS = /^(paid|paid 5k)?$/i

// ───────────── parsing ─────────────

/**
 * Walks one table (date / hulog / utang columns) and returns its loans.
 * `renewMeansSettle`: a "Renew" row inside this table closes the previous loan by deduction.
 */
function parseTable(ws, { from, to, dateCol, hulogCol, utangCol, statusCol, label }, warnings) {
  const loans = []
  let cur = null
  let pending = null // collecting the start of a new loan: { date, utang, renewal }

  const balance = (l) => round2(l.utang - l.payments.reduce((s, p) => s + p.amount, 0))

  for (let r = from; r <= to; r++) {
    const t = text(ws, r, dateCol)
    if (t && /^renew$/i.test(t)) {
      pending = { date: null, utang: null, renewal: true }
      continue
    }

    const d = date(ws, r, dateCol)
    const h = num(ws, r, hulogCol)
    const u = num(ws, r, utangCol)
    const status = statusCol ? text(ws, r, statusCol) : null

    if (!cur && !pending) pending = { date: null, utang: null, renewal: false }

    if (pending) {
      if (d && !pending.date && !(h > 0 && pending.utang)) pending.date = d
      if (u != null && !pending.utang) pending.utang = u
      if (pending.date && pending.utang) {
        const prev = cur
        cur = { date: pending.date, utang: pending.utang, payments: [], renewal: pending.renewal, table: label }
        if (prev && pending.renewal) {
          const left = balance(prev)
          if (left > 0) prev.payments.push({ date: cur.date, amount: left, note: 'Remaining balance settled on renewal' })
        }
        loans.push(cur)
        pending = null
        // A start row normally carries no payment; if it does, fall through and record it.
        if (!(h > 0) || d === cur.date) continue
      } else {
        continue
      }
    }

    if (!(h > 0)) continue
    if (!d) { warnings.push(`${label} row ${r}: payment ${h} has no date — skipped`); continue }

    const before = balance(cur)
    if (u != null && Math.abs(u - before) > 0.005)
      warnings.push(`${label} row ${r}: sheet says ${u} owed before payment, computed ${before}`)
    if (h > before + 0.005) {
      warnings.push(`${label} row ${r}: payment ${h} exceeds balance ${before} — skipped`)
      continue
    }
    cur.payments.push({ date: d, amount: h, note: status && !IGNORED_STATUS.test(status) ? `Excel status: ${status}` : null })
  }
  return loans
}

function findHeaderCols(ws) {
  const cols = {}
  for (let c = 1; c <= ws.columnCount; c++) {
    const t = (text(ws, 1, c) ?? '').toLowerCase()
    if ((t === 'date' || t === 'start') && !cols.date) cols.date = c
    else if (t === 'hulog') cols.hulog = c
    else if (t === 'utang') cols.utang = c
    else if (t === 'status' && !cols.status) cols.status = c
    else if (t === 'due date') cols.due = c
  }
  return cols
}

/** Side tables: a cell reading "Other utang" / "Other" heads a date|hulog|utang table starting at that column. */
function findSideTables(ws, mainCols) {
  const heads = []
  ws.eachRow((row, r) => {
    row.eachCell((cell, c) => {
      if (c <= (mainCols.status ?? mainCols.utang)) return
      const t = text(ws, r, c)
      if (t && /^other( utang)?$/i.test(t) && !heads.some((h) => h.row === r)) heads.push({ row: r, col: c })
    })
  })
  return heads.map((h, i) => ({
    from: h.row + 1,
    to: (heads.find((x, j) => j > i && x.col === h.col)?.row ?? ws.rowCount + 1) - 1,
    dateCol: h.col, hulogCol: h.col + 1, utangCol: h.col + 2, label: `${ws.name} / other utang @${h.row}`,
    side: true,
  }))
}

function parseSheet(ws) {
  const warnings = []
  const [name, ...rest] = ws.name.split(' - ')
  const borrower = { firstName: name.trim(), notes: rest.length ? rest.join(' - ').trim() : null }
  const cols = findHeaderCols(ws)
  const extraNotes = []

  // Sheet-wide hints (due dates, payment schedules) become loan notes / term.
  let termMonths = DEFAULT_TERM_MONTHS
  const due = cols.due ? date(ws, 2, cols.due) : null

  let loans = []
  if (cols.date && cols.hulog && cols.utang) {
    loans = parseTable(ws, {
      from: 2, to: ws.rowCount, dateCol: cols.date, hulogCol: cols.hulog, utangCol: cols.utang,
      statusCol: cols.status, label: ws.name,
    }, warnings)
    for (const t of findSideTables(ws, cols)) {
      const side = parseTable(ws, t, warnings)
      side.forEach((l) => (l.side = true))
      loans.push(...side)
    }
  }

  // ── Per-sheet adjustments for layouts the generic parser can't infer ──
  switch (ws.name) {
    case 'Ayie': {
      // "bukod na utang" side table: three separate loans (confirmed by owner). Payment dates weren't recorded.
      loans.push(
        { date: '2026-09-13', utang: 10000, side: true, payments: [{ date: '2026-09-13', amount: 5000, note: 'Excel: "paid 5k" — payment date not recorded' }] },
        { date: '2026-09-21', utang: 5000, side: true, payments: [{ date: '2026-09-21', amount: 5000, note: 'Excel: "paid" — payment date not recorded' }] },
        { date: '2026-10-17', utang: 5000, side: true, payments: [] },
      )
      break
    }
    case 'Bikol': {
      // Row 4 (2026-10-03) raises the balance from 1,000 to 3,400: an extra 2,400 borrowed, repaid on 10-25.
      // The generic parser can't see that, so both loans are spelled out from the sheet's rows.
      loans = [
        { date: date(ws, 2, 1), utang: num(ws, 2, 3), payments: [{ date: date(ws, 3, 1), amount: num(ws, 3, 2), note: null }] },
        { date: date(ws, 4, 1), utang: num(ws, 4, 4) - (num(ws, 3, 3) - num(ws, 3, 2)), side: true, note: 'Added on top of the existing loan',
          payments: [{ date: date(ws, 5, 1), amount: num(ws, 5, 2), note: null }] },
      ]
      warnings.length = 0
      break
    }
    case 'MokMok':
      extraNotes.push(`Schedule: ${text(ws, 2, 7)} (sahod ${text(ws, 2, 8)})`)
      termMonths = 4
      break
    case 'Nai yob':
      borrower.notes = `Tricycle: ${date(ws, 3, 1)} to ${date(ws, 3, 2)}; Appliances: until ${date(ws, 3, 7)} (no amounts in Excel)`
      break
    case 'Gener':
    case 'Jordan':
      extraNotes.push(`Payment term: ${text(ws, 2, 8)}`)
      if (ws.name === 'Jordan') extraNotes.push(`Due ${date(ws, 2, 9)}`)
      break
    case 'Randy - boss andy ka work':
      extraNotes.push(`Payments start ${date(ws, 2, 8)}`)
      break
    case 'Marvin - Boss andy':
      extraNotes.push(`Payments start ${date(ws, 2, 7)}`)
      break
  }

  loans.forEach((l, i) => {
    const notes = [`From Excel sheet "${ws.name}"`]
    if (l.renewal) notes.push('renewal')
    if (l.side) notes.push('separate loan')
    if (l.note) notes.push(l.note)
    if (i === 0) {
      if (due) notes.push(`due ${due}`)
      notes.push(...extraNotes)
    }
    l.notes = notes.join(' · ')
    l.termMonths = i === 0 && due ? Math.max(1, Math.ceil(monthsBetween(l.date, due) - 0.05)) : termMonths
  })

  return { sheet: ws.name, borrower, loans, warnings }
}

// ───────────── API ─────────────

async function call(method, path, body) {
  const res = await fetch(API + path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
  const data = res.status === 204 ? null : await res.json().catch(() => null)
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${JSON.stringify(data?.errors ?? data?.detail ?? data)}`)
  return data
}

// ───────────── main ─────────────

const wb = new ExcelJS.Workbook()
await wb.xlsx.readFile(FILE)
const people = []
wb.eachSheet((ws) => people.push(parseSheet(ws)))

const fmt = (n) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
let totals = { borrowers: 0, loans: 0, payments: 0, owed: 0, paid: 0 }

for (const p of people) {
  console.log(`\n■ ${p.borrower.firstName}${p.borrower.notes ? `  (${p.borrower.notes})` : ''}`)
  if (!p.loans.length) console.log('    no loans')
  for (const l of p.loans) {
    const paid = round2(l.payments.reduce((s, x) => s + x.amount, 0))
    const bal = round2(l.utang - paid)
    console.log(`    ${l.date}  owed ${fmt(l.utang).padStart(10)}  lent ${fmt(round2(l.utang / 1.2)).padStart(10)}  ` +
      `${String(l.payments.length).padStart(2)} payments ${fmt(paid).padStart(10)}  balance ${fmt(bal).padStart(10)}  ${l.termMonths} mo  ${bal <= 0 ? 'PAID OFF' : 'ACTIVE'}`)
    for (const x of l.payments.filter((x) => x.note)) console.log(`        ${x.date} ${fmt(x.amount)} — ${x.note}`)
    totals.loans++; totals.payments += l.payments.length; totals.owed += l.utang; totals.paid += paid
  }
  for (const w of p.warnings) console.log(`    ⚠ ${w}`)
  totals.borrowers++
}
console.log(`\nTotal: ${totals.borrowers} borrowers, ${totals.loans} loans, ${totals.payments} payments, ` +
  `owed ${fmt(totals.owed)}, paid ${fmt(totals.paid)}, outstanding ${fmt(totals.owed - totals.paid)}`)

if (!APPLY) {
  console.log('\nDry run only. Re-run with --apply to import.')
  process.exit(0)
}

const existing = await call('GET', '/api/borrowers')
if (existing.length) {
  console.error(`\nThe database already has ${existing.length} borrowers. Clear it first so the import isn't duplicated.`)
  process.exit(1)
}

for (const p of people) {
  const { id: borrowerId } = await call('POST', '/api/borrowers', p.borrower)
  for (const l of p.loans) {
    const { id: loanId } = await call('POST', '/api/loans', {
      borrowerId, principal: round2(l.utang / 1.2), interestRate: FLAT_RATE, interestType: 'Flat',
      term: l.termMonths, termUnit: 'Months', startDate: l.date, status: 'Active', notes: l.notes,
    })
    const { loan } = await call('GET', `/api/loans/${loanId}`)
    if (Math.abs(loan.totalDue - l.utang) > 0.005)
      throw new Error(`${p.sheet}: total owed ${loan.totalDue} doesn't match Excel ${l.utang}`)
    for (const x of l.payments)
      await call('POST', `/api/loans/${loanId}/payments`, { amount: x.amount, paidOn: x.date, note: x.note })
  }
  console.log(`✓ ${p.borrower.firstName}`)
}
console.log('\nImport finished.')
