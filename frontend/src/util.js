import { useCallback, useEffect, useState } from 'react'

const money = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
export const fmtMoney = (n) => money.format(n ?? 0)

export const fmtDate = (d) =>
  d ? new Date(d.length === 10 ? `${d}T00:00:00` : d).toLocaleDateString(undefined, { dateStyle: 'medium' }) : '—'

export const fmtDateTime = (d) =>
  d ? new Date(d).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—'

export const today = () => {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

export const statusLabel = (s) => (s === 'PaidOff' ? 'Paid off' : s)

// Interest type is hidden in the UI for now (all loans default to flat), so the rate shows on its own.
export const rateLabel = (loan) => `${loan.interestRate}%`

export const fullName = (b) => `${b.firstName} ${b.lastName ?? ''}`.trim()

export const TERM_UNITS = {
  Days: { one: 'day', many: 'days', short: 'd', perYear: 365 },
  Weeks: { one: 'week', many: 'weeks', short: 'wk', perYear: 52 },
  Months: { one: 'month', many: 'months', short: 'mo', perYear: 12 },
}

/** Term unit for loans repaid whenever the borrower can: no installments, no due date. */
export const NO_DEADLINE = 'NoDeadline'

export const termLabel = (loan, short = false) => {
  if (loan.termUnit === NO_DEADLINE) return short ? 'No deadline' : 'No specific deadline'
  const u = TERM_UNITS[loan.termUnit] ?? TERM_UNITS.Months
  return short ? `${loan.term} ${u.short}` : `${loan.term} ${loan.term === 1 ? u.one : u.many}`
}

/** "Per week" / "Per month". */
export const perUnitLabel = (termUnit) => `Per ${(TERM_UNITS[termUnit] ?? TERM_UNITS.Months).one}`

/** Loans whose balance still counts as owed (mirrors LoanMath.CountsAsOwed in the API). */
export const countsAsOwed = (loan) => loan.status === 'Active' || loan.status === 'Defaulted'

/** Mirrors the API's LoanTerms so forms can preview totals before saving. */
export function loanTotals(principal, rate, term, termUnit, interestType) {
  const p = Number(principal), r = Number(rate), n = parseInt(term, 10)
  const round = (x) => Math.round(x * 100) / 100
  if (termUnit === NO_DEADLINE) {
    // Nothing to spread over, so interest is charged once and there's no per-period amount.
    if (!(p > 0) || !(r >= 0)) return null
    const total = round(p * (1 + r / 100))
    return { installment: null, total, interest: total - p }
  }
  if (!(p > 0) || !(n > 0) || !(r >= 0)) return null
  let total, installment
  if (interestType === 'Flat' || r === 0) {
    total = interestType === 'Flat' ? round(p * (1 + r / 100)) : p
    installment = round(total / n)
  } else {
    const m = r / 100 / (TERM_UNITS[termUnit] ?? TERM_UNITS.Months).perYear
    installment = round((p * m) / (1 - Math.pow(1 + m, -n)))
    total = installment * n
  }
  return { installment, total, interest: total - p }
}

/** Runs an async loader, re-running when deps change. Returns { data, error, loading, reload }. */
export function useAsync(loader, deps) {
  const [state, setState] = useState({ data: null, error: null, loading: true })
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(loader, deps)

  const reload = useCallback(() => {
    let cancelled = false
    setState((s) => ({ ...s, loading: true }))
    run()
      .then((data) => !cancelled && setState({ data, error: null, loading: false }))
      .catch((error) => !cancelled && setState((s) => ({ ...s, error, loading: false })))
    return () => (cancelled = true)
  }, [run])

  useEffect(() => reload(), [reload])
  return { ...state, reload }
}
