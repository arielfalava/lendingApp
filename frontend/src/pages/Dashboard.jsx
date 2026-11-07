import { Link } from 'react-router-dom'
import { api, LOAN_STATUSES } from '../api'
import { HistoryTimeline } from '../components/HistoryTimeline'
import { ErrorBanner, Loading, StatusBadge } from '../components/ui'
import { fmtMoney, useAsync } from '../util'

export default function Dashboard() {
  const { data, error, loading } = useAsync(() => api.dashboard(), [])

  return (
    <>
      <header className="page-head">
        <h1>Dashboard</h1>
      </header>
      <ErrorBanner error={error} />
      {loading && !data ? <Loading /> : data && (
        <>
          <section className="stats">
            <div className="stat"><span>Remaining balance</span><strong className="num">{fmtMoney(data.outstandingBalance)}</strong></div>
            <div className="stat"><span>Borrowers</span><strong className="num">{data.borrowerCount}</strong></div>
          </section>
          <section className="card">
            <h2>Loans by status</h2>
            <div className="status-row">
              {LOAN_STATUSES.map((s) => (
                <Link key={s} to={`/loans?status=${s}`} className="status-chip">
                  <StatusBadge status={s} />
                  <strong className="num">{data.loansByStatus[s] ?? 0}</strong>
                </Link>
              ))}
            </div>
          </section>
        </>
      )}
      <section className="card">
        <div className="card-head">
          <h2>Recent activity</h2>
          <Link to="/history" className="small-link">All history</Link>
        </div>
        <HistoryTimeline filters={{ pageSize: 8 }} showLink />
      </section>
    </>
  )
}
