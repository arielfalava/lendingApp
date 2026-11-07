import { useMemo } from 'react'
import { api, LOAN_STATUSES } from '../api'
import { fmtMoney, fullName, loanTotals, NO_DEADLINE, perUnitLabel, statusLabel, today, useAsync } from '../util'
import { ErrorBanner, Field, Modal, useForm } from './ui'

function FormActions({ saving, onCancel, label = 'Save' }) {
  return (
    <div className="modal-actions">
      <button type="button" className="btn" onClick={onCancel}>Cancel</button>
      <button type="submit" className="btn btn-primary" disabled={saving}>{saving ? 'Saving…' : label}</button>
    </div>
  )
}

export function BorrowerForm({ borrower, onClose, onSaved }) {
  const f = useForm(
    {
      firstName: borrower?.firstName ?? '',
      lastName: borrower?.lastName ?? '',
      email: borrower?.email ?? '',
      phone: borrower?.phone ?? '',
      address: borrower?.address ?? '',
      notes: borrower?.notes ?? '',
    },
    async (v) => {
      const res = borrower ? await api.updateBorrower(borrower.id, v) : await api.createBorrower(v)
      onSaved(res?.id ?? borrower.id)
    },
  )

  return (
    <Modal title={borrower ? 'Edit borrower' : 'New borrower'} onClose={onClose}>
      <form onSubmit={f.onSubmit} noValidate>
        <ErrorBanner error={f.formError} />
        <div className="grid-2">
          <Field label="Name" error={f.errors.firstName}><input {...f.bind('firstName')} autoFocus /></Field>
          <Field label="Last name (optional)" error={f.errors.lastName}><input {...f.bind('lastName')} /></Field>
        </div>
        <div className="grid-2">
          <Field label="Phone (optional)" error={f.errors.phone}><input {...f.bind('phone')} /></Field>
          <Field label="Email (optional)" error={f.errors.email}><input type="email" {...f.bind('email')} /></Field>
        </div>
        <Field label="Address (optional)" error={f.errors.address}><input {...f.bind('address')} /></Field>
        <Field label="Notes (optional)" error={f.errors.notes}><textarea rows={2} {...f.bind('notes')} /></Field>
        <FormActions saving={f.saving} onCancel={onClose} />
      </form>
    </Modal>
  )
}


const NEW_BORROWER = '__new__'

export function LoanForm({ loan, defaultBorrowerId, onClose, onSaved }) {
  const borrowers = useAsync(() => api.borrowers(), [])
  const f = useForm(
    {
      borrowerId: loan?.borrowerId ?? defaultBorrowerId ?? '',
      newBorrowerName: '',
      principal: loan?.principal ?? '',
      interestRate: loan?.interestRate ?? 20,
      interestType: loan?.interestType ?? 'Flat',
      term: loan?.term || 8,
      termUnit: loan?.termUnit ?? 'Weeks',
      startDate: loan?.startDate ?? today(),
      status: loan?.status ?? 'Active',
      notes: loan?.notes ?? '',
    },
    async (v) => {
      const isNew = v.borrowerId === NEW_BORROWER
      const input = {
        ...v,
        borrowerId: isNew || !v.borrowerId ? null : v.borrowerId,
        newBorrowerName: isNew ? v.newBorrowerName : null,
        principal: Number(v.principal) || 0,
        interestRate: Number(v.interestRate) || 0,
        term: v.termUnit === NO_DEADLINE ? 0 : parseInt(v.term, 10) || 0,
      }
      const res = loan ? await api.updateLoan(loan.id, input) : await api.createLoan(input)
      onSaved(res?.id ?? loan.id)
    },
  )

  // Live preview of the installment, using the same formulas as the API.
  const { principal, interestRate, term, termUnit, interestType, borrowerId } = f.values
  const preview = useMemo(
    () => loanTotals(principal, interestRate, term, termUnit, interestType),
    [principal, interestRate, term, termUnit, interestType],
  )
  const isNewBorrower = borrowerId === NEW_BORROWER
  const noDeadline = termUnit === NO_DEADLINE

  return (
    <Modal title={loan ? 'Edit loan' : 'New loan'} onClose={onClose}>
      <form onSubmit={f.onSubmit} noValidate>
        <ErrorBanner error={f.formError} />
        <Field label="Borrower" error={isNewBorrower ? undefined : f.errors.borrowerId}>
          <select {...f.bind('borrowerId')} autoFocus={!loan}>
            <option value="">Select a borrower…</option>
            <option value={NEW_BORROWER}>+ New borrower</option>
            {borrowers.data?.map((b) => (
              <option key={b.id} value={b.id}>{fullName(b)}{b.notes ? ` — ${b.notes}` : ''}</option>
            ))}
          </select>
        </Field>
        {isNewBorrower && (
          <Field
            label="New borrower name"
            error={f.errors.newBorrowerName ?? f.errors.borrowerId}
            hint="They'll be added to your borrowers when you save. Add phone or notes later from their page."
          >
            <input {...f.bind('newBorrowerName')} autoFocus />
          </Field>
        )}
        {/* Interest type is hidden for now: every loan is flat (rate added once).
            To bring it back, uncomment this field; the API already supports "Amortized".
        <Field label="Interest type" error={f.errors.interestType}>
          <select {...f.bind('interestType')}>
            <option value="Flat">Flat (added once)</option>
            <option value="Amortized">Amortized (per year)</option>
          </select>
        </Field>
        */}
        <div className="grid-2">
          <Field label="Principal (amount lent)" error={f.errors.principal}>
            <input type="number" min="0" step="0.01" inputMode="decimal" {...f.bind('principal')} />
          </Field>
          <Field label="Interest (%)" error={f.errors.interestRate}>
            <input type="number" min="0" max="100" step="0.01" inputMode="decimal" {...f.bind('interestRate')} />
          </Field>
        </div>
        <div className="grid-2">
          <Field
            label="Term"
            error={f.errors.term ?? f.errors.termUnit}
            hint={noDeadline ? 'Repaid whenever the borrower can. No installments or due date.' : undefined}
          >
            <div className="input-group">
              {!noDeadline && (
                <input type="number" min="1" step="1" inputMode="numeric" aria-label="Number of payments" {...f.bind('term')} />
              )}
              <select aria-label="Term unit" {...f.bind('termUnit')} className={noDeadline ? 'grow' : undefined}>
                <option value="Days">Days</option>
                <option value="Weeks">Weeks</option>
                <option value="Months">Months</option>
                <option value={NO_DEADLINE}>No specific deadline</option>
              </select>
            </div>
          </Field>
          <Field label="Start date" error={f.errors.startDate}><input type="date" {...f.bind('startDate')} /></Field>
        </div>
        <div className="grid-2">
          <Field label="Status" error={f.errors.status}>
            <select {...f.bind('status')}>
              {LOAN_STATUSES.map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
            </select>
          </Field>
        </div>
        <Field label="Notes" error={f.errors.notes}><textarea rows={2} {...f.bind('notes')} /></Field>
        {preview && (
          <div className="preview">
            {preview.installment != null && (
              <div><span>{perUnitLabel(termUnit)}</span><strong className="num">{fmtMoney(preview.installment)}</strong></div>
            )}
            <div><span>Total repayable</span><strong className="num">{fmtMoney(preview.total)}</strong></div>
            <div><span>Total interest</span><strong className="num">{fmtMoney(preview.interest)}</strong></div>
          </div>
        )}
        <FormActions saving={f.saving} onCancel={onClose} label={loan ? 'Save' : 'Create loan'} />
      </form>
    </Modal>
  )
}

export function PaymentForm({ loan, payment, onClose, onSaved }) {
  // No-deadline loans have no installment, so suggest the full balance instead.
  const due = loan.balance + (payment?.amount ?? 0)
  const suggested = loan.installment == null ? due : Math.min(loan.installment, due)
  const f = useForm(
    {
      amount: payment?.amount ?? (suggested > 0 ? suggested.toFixed(2) : ''),
      paidOn: payment?.paidOn ?? today(),
      note: payment?.note ?? '',
    },
    async (v) => {
      const input = { ...v, amount: Number(v.amount) || 0 }
      if (payment) await api.updatePayment(payment.id, input)
      else await api.recordPayment(loan.id, input)
      onSaved()
    },
  )

  return (
    <Modal title={payment ? 'Edit payment' : 'Record payment'} onClose={onClose}>
      <form onSubmit={f.onSubmit} noValidate>
        <ErrorBanner error={f.formError} />
        <div className="grid-2">
          <Field
            label="Amount"
            error={f.errors.amount}
            hint={`Balance: ${fmtMoney(loan.balance + (payment?.amount ?? 0))}`}
          >
            <input type="number" min="0" step="0.01" inputMode="decimal" {...f.bind('amount')} autoFocus />
          </Field>
          <Field label="Paid on" error={f.errors.paidOn}><input type="date" {...f.bind('paidOn')} /></Field>
        </div>
        <Field label="Note" error={f.errors.note}><input {...f.bind('note')} /></Field>
        <FormActions saving={f.saving} onCancel={onClose} label={payment ? 'Save' : 'Record'} />
      </form>
    </Modal>
  )
}
