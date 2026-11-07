import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { fmtDateTime, fmtMoney, statusLabel, useAsync } from '../util'
import { Empty, ErrorBanner, Loading } from './ui'

const FIELD_LABELS = {
  BorrowerId: 'Borrower', FirstName: 'First name', LastName: 'Last name', InterestRate: 'Interest %',
  InterestType: 'Interest type', TermMonths: 'Term (months)', Term: 'Term', TermUnit: 'Term unit', StartDate: 'Start date', PaidOn: 'Paid on', LoanId: 'Loan',
}

function formatValue(field, value) {
  if (value === null || value === undefined || value === '') return <em className="muted">empty</em>
  if (field === 'Principal' || field === 'Amount') return fmtMoney(Number(value))
  if (field === 'Status') return statusLabel(value)
  if (/Id$/.test(field)) return <code title={value}>{value.slice(0, 8)}</code>
  return value
}

function Change({ c, action }) {
  return (
    <tr>
      <th>{FIELD_LABELS[c.field] ?? c.field}</th>
      {action === 'Updated' ? (
        <td>
          <span className="old">{formatValue(c.field, c.oldValue)}</span>
          <span className="arrow" aria-hidden>→</span>
          <span className="new">{formatValue(c.field, c.newValue)}</span>
        </td>
      ) : (
        <td>{formatValue(c.field, action === 'Deleted' ? c.oldValue : c.newValue)}</td>
      )}
    </tr>
  )
}

function entityLink(e) {
  if (e.action === 'Deleted') return null
  if (e.entityType === 'Borrower') return `/borrowers/${e.entityId}`
  if (e.loanId) return `/loans/${e.loanId}`
  return null
}

export function HistoryItem({ entry, showLink }) {
  const [open, setOpen] = useState(entry.action === 'Updated')
  const link = showLink && entityLink(entry)
  return (
    <li className={`timeline-item action-${entry.action.toLowerCase()}`}>
      <div className="timeline-dot" aria-hidden />
      <div className="timeline-body">
        <div className="timeline-head">
          <span className={`pill pill-${entry.action.toLowerCase()}`}>{entry.action}</span>
          <span className="timeline-summary">{entry.summary}</span>
          {link && <Link to={link} className="small-link">View</Link>}
          <time className="muted" dateTime={entry.timestamp}>{fmtDateTime(entry.timestamp)}</time>
        </div>
        {entry.changes.length > 0 && (
          <>
            <button className="link-btn" onClick={() => setOpen((o) => !o)}>
              {open ? 'Hide' : 'Show'} {entry.changes.length} field{entry.changes.length > 1 ? 's' : ''}
            </button>
            {open && (
              <table className="changes">
                <tbody>{entry.changes.map((c) => <Change key={c.field} c={c} action={entry.action} />)}</tbody>
              </table>
            )}
          </>
        )}
      </div>
    </li>
  )
}

/** History for a scope (e.g. { loanId } or { borrowerId }). `version` forces a refresh after edits. */
export function HistoryTimeline({ filters, version, showLink = false }) {
  const key = JSON.stringify(filters)
  const { data, error, loading } = useAsync(() => api.history({ ...filters, pageSize: 100 }), [key, version])

  if (error) return <ErrorBanner error={error} />
  if (loading && !data) return <Loading />
  if (!data?.items.length) return <Empty>No history yet.</Empty>
  return (
    <ul className="timeline">
      {data.items.map((e) => <HistoryItem key={e.id} entry={e} showLink={showLink} />)}
    </ul>
  )
}
