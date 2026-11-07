import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api } from '../api'
import { LoanForm, PaymentForm } from '../components/forms'
import { HistoryTimeline } from '../components/HistoryTimeline'
import { Empty, ErrorBanner, Loading, StatusBadge } from '../components/ui'
import { fmtDate, fmtMoney, perUnitLabel, rateLabel, termLabel, useAsync } from '../util'

const PAYABLE = ['Active', 'Defaulted', 'PaidOff']

export default function LoanDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [version, setVersion] = useState(0)
  const [modal, setModal] = useState(null) // { type: 'edit' } | { type: 'payment', payment? }
  const [actionError, setActionError] = useState(null)
  const { data, error } = useAsync(() => api.loan(id), [id, version])

  const refresh = () => { setModal(null); setActionError(null); setVersion((v) => v + 1) }

  const run = async (fn) => {
    try { await fn(); refresh() } catch (e) { setActionError(e) }
  }

  const deleteLoan = async () => {
    if (!confirm('Delete this loan and all its payments? This cannot be undone.')) return
    try { await api.deleteLoan(id); navigate('/loans') } catch (e) { setActionError(e) }
  }

  const exportExcel = async () => {
    setActionError(null)
    try { await api.exportLoan(id) } catch (e) { setActionError(e) }
  }

  if (error) return <ErrorBanner error={error} />
  if (!data) return <Loading />
  const { loan, payments } = data
  const pct = loan.totalDue > 0 ? Math.min(100, (loan.totalPaid / loan.totalDue) * 100) : 0
  const canPay = PAYABLE.includes(loan.status) && loan.balance > 0

  return (
    <>
      <header className="page-head">
        <div>
          <p className="crumb">Loan · <Link to={`/borrowers/${loan.borrowerId}`}>{loan.borrowerName}</Link></p>
          <h1>{fmtMoney(loan.principal)} <StatusBadge status={loan.status} /></h1>
        </div>
        <div className="actions">
          <button className="btn" onClick={exportExcel} title="This loan and all its payments">Export Excel</button>
          <button className="btn" onClick={() => setModal({ type: 'edit' })}>Edit</button>
          <button className="btn btn-danger" onClick={deleteLoan}>Delete</button>
          <button
            className="btn btn-primary"
            disabled={!canPay}
            title={canPay ? '' : loan.balance <= 0 ? 'Loan is fully paid' : 'Set the loan to Active to record payments'}
            onClick={() => setModal({ type: 'payment' })}
          >
            Record payment
          </button>
        </div>
      </header>
      <ErrorBanner error={actionError} />

      {loan.status === 'Pending' && (
        <div className="banner banner-info">
          This loan is pending. Approve it to start collecting payments.{' '}
          <button
            className="link-btn"
            onClick={() => run(() => api.updateLoan(loan.id, { ...loan, status: 'Active' }))}
          >
            Activate now
          </button>
        </div>
      )}

      <section className="card">
        <div className="progress-head">
          <span><strong className="num">{fmtMoney(loan.totalPaid)}</strong> paid of <span className="num">{fmtMoney(loan.totalDue)}</span></span>
          <span className="num">{pct.toFixed(0)}%</span>
        </div>
        <div className="progress" role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100}>
          <div style={{ width: `${pct}%` }} />
        </div>
        <dl className="details">
          <div><dt>Remaining balance</dt><dd className="num"><strong>{fmtMoney(loan.balance)}</strong></dd></div>
          {loan.installment != null && <div><dt>{perUnitLabel(loan.termUnit)}</dt><dd className="num">{fmtMoney(loan.installment)}</dd></div>}
          <div><dt>Interest rate</dt><dd className="num">{rateLabel(loan)}</dd></div>
          <div><dt>Term</dt><dd className="num">{termLabel(loan)}</dd></div>
          <div><dt>Start date</dt><dd>{fmtDate(loan.startDate)}</dd></div>
          <div><dt>Maturity date</dt><dd>{loan.maturityDate ? fmtDate(loan.maturityDate) : 'No deadline'}</dd></div>
          <div className="span-all"><dt>Notes</dt><dd>{loan.notes ?? '—'}</dd></div>
        </dl>
      </section>

      <section className="card table-card">
        <h2 className="card-pad">Payments <span className="muted">({payments.length})</span></h2>
        {payments.length === 0 ? <Empty>No payments recorded.</Empty> : (
          <table className="table">
            <thead><tr><th>Paid on</th><th className="r">Amount</th><th>Note</th><th className="r">Actions</th></tr></thead>
            <tbody>
              {payments.map((p) => (
                <tr key={p.id}>
                  <td>{fmtDate(p.paidOn)}</td>
                  <td className="r num">{fmtMoney(p.amount)}</td>
                  <td>{p.note ?? '—'}</td>
                  <td className="r">
                    <button className="link-btn" onClick={() => setModal({ type: 'payment', payment: p })}>Edit</button>
                    <button
                      className="link-btn danger"
                      onClick={() => confirm('Delete this payment?') && run(() => api.deletePayment(p.id))}
                    >
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="card">
        <h2>History</h2>
        <HistoryTimeline filters={{ loanId: id }} version={version} />
      </section>

      {modal?.type === 'edit' && <LoanForm loan={loan} onClose={() => setModal(null)} onSaved={refresh} />}
      {modal?.type === 'payment' && (
        <PaymentForm loan={loan} payment={modal.payment} onClose={() => setModal(null)} onSaved={refresh} />
      )}
    </>
  )
}
