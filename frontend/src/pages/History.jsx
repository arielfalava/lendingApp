import { useState } from 'react'
import { api } from '../api'
import { HistoryItem } from '../components/HistoryTimeline'
import { Empty, ErrorBanner, Loading } from '../components/ui'
import { useAsync } from '../util'

const PAGE_SIZE = 20

export default function History() {
  const [entityType, setEntityType] = useState('')
  const [action, setAction] = useState('')
  const [page, setPage] = useState(1)
  const { data, error, loading } = useAsync(
    () => api.history({ entityType, action, page, pageSize: PAGE_SIZE }),
    [entityType, action, page],
  )
  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1
  const filter = (setter) => (e) => { setter(e.target.value); setPage(1) }

  return (
    <>
      <header className="page-head">
        <h1>History</h1>
      </header>
      <p className="muted lead">Every create, update and delete is recorded automatically, with the before and after values.</p>

      <div className="toolbar">
        <select value={entityType} onChange={filter(setEntityType)} aria-label="Record type">
          <option value="">All records</option>
          <option value="Borrower">Borrowers</option>
          <option value="Loan">Loans</option>
          <option value="Payment">Payments</option>
        </select>
        <select value={action} onChange={filter(setAction)} aria-label="Action">
          <option value="">All actions</option>
          <option value="Created">Created</option>
          <option value="Updated">Updated</option>
          <option value="Deleted">Deleted</option>
        </select>
        {data && <span className="muted">{data.total} entr{data.total === 1 ? 'y' : 'ies'}</span>}
      </div>

      <ErrorBanner error={error} />
      <section className="card">
        {loading && !data ? <Loading /> : !data?.items.length ? <Empty>No matching history.</Empty> : (
          <ul className="timeline">
            {data.items.map((e) => <HistoryItem key={e.id} entry={e} showLink />)}
          </ul>
        )}
      </section>

      {pages > 1 && (
        <nav className="pager" aria-label="Pagination">
          <button className="btn" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</button>
          <span className="muted">Page {page} of {pages}</span>
          <button className="btn" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>Next</button>
        </nav>
      )}
    </>
  )
}
