import { useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api, LOAN_STATUSES } from '../api'
import { LoanForm } from '../components/forms'
import { ImportLoansDialog } from '../components/ImportLoans'
import { Empty, ErrorBanner, Loading, StatusBadge } from '../components/ui'
import { countsAsOwed, fmtDate, fmtMoney, rateLabel, statusLabel, termLabel, useAsync } from '../util'

export function LoansTable({ loans, hideBorrower = false, emptyText = 'No loans found.' }) {
  const navigate = useNavigate()
  if (loans.error) return <ErrorBanner error={loans.error} />
  if (loans.loading && !loans.data) return <Loading />
  if (!loans.data?.length) return <Empty>{emptyText}</Empty>

  const owed = loans.data.filter(countsAsOwed)
  const remaining = owed.reduce((sum, l) => sum + l.balance, 0)

  return (
    <>
      <div className="table-summary">
        <span className="muted">Remaining balance</span>
        <strong className="num">{fmtMoney(remaining)}</strong>
        <span className="muted">across {owed.length} active loan{owed.length === 1 ? '' : 's'}</span>
      </div>
      <table className="table">
        <thead>
          <tr>
            {!hideBorrower && <th>Borrower</th>}
            <th className="r">Principal</th><th className="r">Rate</th><th className="r">Term</th>
            <th>Start</th><th className="r">Paid</th><th className="r">Remaining balance</th><th>Status</th><th>Remarks</th>
          </tr>
        </thead>
        <tbody>
          {loans.data.map((l) => (
            <tr key={l.id} className="clickable" onClick={() => navigate(`/loans/${l.id}`)}>
              {!hideBorrower && (
                <td><Link to={`/loans/${l.id}`} onClick={(e) => e.stopPropagation()}>{l.borrowerName}</Link></td>
              )}
              <td className="r num">{fmtMoney(l.principal)}</td>
              <td className="r num">{rateLabel(l)}</td>
              <td className="r num">{termLabel(l, true)}</td>
              <td>{fmtDate(l.startDate)}</td>
              <td className="r num">{fmtMoney(l.totalPaid)}</td>
              <td className="r num"><strong>{fmtMoney(l.balance)}</strong></td>
              <td><StatusBadge status={l.status} /></td>
              <td className="truncate muted" title={l.notes ?? ''}>{l.notes ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}

export default function Loans() {
  const [params, setParams] = useSearchParams()
  const status = params.get('status') ?? ''
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)
  const [importing, setImporting] = useState(false)
  const [exportError, setExportError] = useState(null)
  const navigate = useNavigate()
  const loans = useAsync(() => api.loans({ status, search }), [status, search])

  const exportExcel = async () => {
    setExportError(null)
    try { await api.exportLoans({ status, search }) } catch (e) { setExportError(e) }
  }

  return (
    <>
      <header className="page-head">
        <h1>Loans</h1>
        <div className="actions">
          <button className="btn" onClick={() => setImporting(true)}>Import Excel</button>
          <button className="btn" onClick={exportExcel} title="Exports the loans currently shown">Export Excel</button>
          <button className="btn btn-primary" onClick={() => setCreating(true)}>New loan</button>
        </div>
      </header>
      <ErrorBanner error={exportError} />

      <div className="toolbar">
        <input type="search" placeholder="Search borrower or notes…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <div className="segmented" role="tablist" aria-label="Filter by status">
          {['', ...LOAN_STATUSES].map((s) => (
            <button
              key={s || 'all'}
              role="tab"
              aria-selected={status === s}
              className={status === s ? 'active' : ''}
              onClick={() => setParams(s ? { status: s } : {})}
            >
              {s ? statusLabel(s) : 'All'}
            </button>
          ))}
        </div>
      </div>

      <div className="card table-card">
        <LoansTable loans={loans} />
      </div>

      {creating && <LoanForm onClose={() => setCreating(false)} onSaved={(id) => navigate(`/loans/${id}`)} />}
      {importing && <ImportLoansDialog onClose={() => setImporting(false)} onImported={loans.reload} />}
    </>
  )
}
