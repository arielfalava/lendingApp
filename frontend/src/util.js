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

export const rateLabel = (loan) => `${loan.interestRate}%${loan.interestType === 'Flat' ? ' flat' : ' / yr'}`

export const fullName = (b) => `${b.firstName} ${b.lastName ?? ''}`.trim()

/** Mirrors the API's LoanMath so forms can preview totals before saving. */
export function loanTotals(principal, rate, termMonths, interestType) {
  const p = Number(principal), r = Number(rate), n = parseInt(termMonths, 10)
  if (!(p > 0) || !(n > 0) || !(r >= 0)) return null
  const round = (x) => Math.round(x * 100) / 100
  let total, monthly
  if (interestType === 'Flat' || r === 0) {
    total = interestType === 'Flat' ? round(p * (1 + r / 100)) : p
    monthly = round(total / n)
  } else {
    const m = r / 100 / 12
    monthly = round((p * m) / (1 - Math.pow(1 + m, -n)))
    total = monthly * n
  }
  return { monthly, total, interest: total - p }
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
