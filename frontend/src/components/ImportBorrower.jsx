import { useState } from 'react'
import { api } from '../api'
import { Modal } from './ui'

/** Upload a borrower workbook (from Export on a borrower's page) to add its loans and payments to this borrower. */
export function ImportBorrowerDialog({ borrowerId, borrowerName, onClose, onImported }) {
  const [file, setFile] = useState(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState(null)
  const [result, setResult] = useState(null)

  const submit = async (e) => {
    e.preventDefault()
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      setResult(await api.importBorrower(borrowerId, file))
      onImported()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const template = async () => {
    try { await api.exportBorrower(borrowerId) } catch (err) { setError(err) }
  }

  return (
    <Modal title={`Import loans and payments for ${borrowerName}`} onClose={onClose}>
      {result ? (
        <>
          <div className="banner banner-success">
            Imported {result.loansCreated} loan{result.loansCreated === 1 ? '' : 's'}
            {' '}and {result.paymentsCreated} payment{result.paymentsCreated === 1 ? '' : 's'}.
          </div>
          <div className="modal-actions">
            <button className="btn btn-primary" onClick={onClose}>Done</button>
          </div>
        </>
      ) : (
        <form onSubmit={submit}>
          <ol className="steps">
            <li>
              Use a file from <button type="button" className="link-btn" onClick={template}>Export</button>
              {' '}as the layout: a "Loans" sheet (one loan per row, numbered by Loan #) and a "Payments" sheet
              (one payment per row, linked by Loan #).
            </li>
            <li>
              Importing <strong>adds</strong> those loans and payments to {borrowerName}. Existing loans are not changed,
              so remove rows you already have to avoid duplicates.
            </li>
            <li>If any row has a problem, nothing is imported and every problem is listed.</li>
          </ol>
          <label className="file-drop">
            <input
              type="file"
              accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
              onChange={(e) => { setFile(e.target.files?.[0] ?? null); setError(null) }}
            />
            <span>{file ? file.name : 'Choose an .xlsx file…'}</span>
          </label>
          {error && (
            <div className="banner banner-error">
              {error.details?.length > 1 ? (
                <>
                  <strong>Nothing was imported. Fix these {error.details.length} problems and try again:</strong>
                  <ul className="error-list">{error.details.map((d) => <li key={d}>{d}</li>)}</ul>
                </>
              ) : error.message}
            </div>
          )}
          <div className="modal-actions">
            <button type="button" className="btn" onClick={onClose}>Cancel</button>
            <button type="submit" className="btn btn-primary" disabled={!file || busy}>{busy ? 'Importing…' : 'Import'}</button>
          </div>
        </form>
      )}
    </Modal>
  )
}
