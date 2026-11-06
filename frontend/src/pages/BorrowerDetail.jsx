import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { api } from '../api'
import { BorrowerForm, LoanForm } from '../components/forms'
import { HistoryTimeline } from '../components/HistoryTimeline'
import { ErrorBanner, Loading } from '../components/ui'
import { LoansTable } from './Loans'
import { fmtDate, fullName, useAsync } from '../util'

export default function BorrowerDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [version, setVersion] = useState(0)
  const [modal, setModal] = useState(null) // 'edit' | 'loan'
  const [actionError, setActionError] = useState(null)

  const borrower = useAsync(() => api.borrower(id), [id, version])
  const loans = useAsync(() => api.loans({ borrowerId: id }), [id, version])
  const refresh = () => { setModal(null); setVersion((v) => v + 1) }

  const remove = async () => {
    if (!confirm('Delete this borrower? This cannot be undone.')) return
    try {
      await api.deleteBorrower(id)
      navigate('/borrowers')
    } catch (e) {
      setActionError(e)
    }
  }

  if (borrower.error) return <ErrorBanner error={borrower.error} />
  if (!borrower.data) return <Loading />
  const b = borrower.data

  return (
    <>
      <header className="page-head">
        <div>
          <p className="crumb">Borrower</p>
          <h1>{fullName(b)}</h1>
        </div>
        <div className="actions">
          <button className="btn" onClick={() => setModal('edit')}>Edit</button>
          <button className="btn btn-danger" onClick={remove}>Delete</button>
          <button className="btn btn-primary" onClick={() => setModal('loan')}>New loan</button>
        </div>
      </header>
      <ErrorBanner error={actionError} />

      <section className="card">
        <dl className="details">
          <div><dt>Email</dt><dd>{b.email ?? '—'}</dd></div>
          <div><dt>Phone</dt><dd>{b.phone ?? '—'}</dd></div>
          <div><dt>Address</dt><dd>{b.address ?? '—'}</dd></div>
          <div className="span-all"><dt>Notes</dt><dd>{b.notes ?? '—'}</dd></div>
          <div><dt>Customer since</dt><dd>{fmtDate(b.createdAt)}</dd></div>
        </dl>
      </section>

      <section className="card table-card">
        <h2 className="card-pad">Loans</h2>
        <LoansTable loans={loans} hideBorrower emptyText="This borrower has no loans yet." />
      </section>

      <section className="card">
        <h2>History</h2>
        <HistoryTimeline filters={{ borrowerId: id }} version={version} showLink />
      </section>

      {modal === 'edit' && <BorrowerForm borrower={b} onClose={() => setModal(null)} onSaved={refresh} />}
      {modal === 'loan' && (
        <LoanForm defaultBorrowerId={id} onClose={() => setModal(null)} onSaved={(loanId) => navigate(`/loans/${loanId}`)} />
      )}
    </>
  )
}
