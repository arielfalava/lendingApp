import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api'
import { BorrowerForm } from '../components/forms'
import { Empty, ErrorBanner, Loading } from '../components/ui'
import { fmtDate, fmtMoney, fullName, useAsync } from '../util'

export default function Borrowers() {
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()
  const { data, error, loading } = useAsync(() => api.borrowers(search), [search])

  return (
    <>
      <header className="page-head">
        <h1>Borrowers</h1>
        <button className="btn btn-primary" onClick={() => setCreating(true)}>New borrower</button>
      </header>

      <div className="toolbar">
        <input type="search" placeholder="Search name or email…" value={search} onChange={(e) => setSearch(e.target.value)} />
      </div>

      <ErrorBanner error={error} />
      <div className="card table-card">
        {loading && !data ? <Loading /> : !data?.length ? (
          <Empty>{search ? 'No borrowers match your search.' : 'No borrowers yet. Add your first one.'}</Empty>
        ) : (
          <table className="table">
            <thead>
              <tr><th>Name</th><th>Phone</th><th>Notes</th><th className="r">Loans</th><th className="r">Active</th><th className="r">Remaining balance</th><th>Since</th></tr>
            </thead>
            <tbody>
              {data.map((b) => (
                <tr key={b.id} className="clickable" onClick={() => navigate(`/borrowers/${b.id}`)}>
                  <td><Link to={`/borrowers/${b.id}`} onClick={(e) => e.stopPropagation()}>{fullName(b)}</Link></td>
                  <td>{b.phone ?? '—'}</td>
                  <td className="truncate" title={b.notes ?? ''}>{b.notes ?? '—'}</td>
                  <td className="r num">{b.loanCount}</td>
                  <td className="r num">{b.activeLoanCount}</td>
                  <td className="r num"><strong>{fmtMoney(b.remainingBalance)}</strong></td>
                  <td>{fmtDate(b.createdAt)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th colSpan={5}>Total remaining balance</th>
                <td className="r num"><strong>{fmtMoney(data.reduce((sum, b) => sum + b.remainingBalance, 0))}</strong></td>
                <td />
              </tr>
            </tfoot>
          </table>
        )}
      </div>

      {creating && (
        <BorrowerForm onClose={() => setCreating(false)} onSaved={(id) => navigate(`/borrowers/${id}`)} />
      )}
    </>
  )
}
