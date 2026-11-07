import { useState } from 'react'
import { api } from '../api'
import { Modal } from './ui'

/** Upload an .xlsx built from the template. The import is all-or-nothing, so errors list every bad row. */
export function ImportLoansDialog({ onClose, onImported }) {
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
      setResult(await api.importLoans(file))
      onImported()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const template = async () => {
    try { await api.downloadImportTemplate() } catch (err) { setError(err) }
  }

  return (
    <Modal title="Import loans from Excel" onClose={onClose}>
      {result ? (
        <>
          <div className="banner banner-success">
            Imported {result.loansCreated} loan{result.loansCreated === 1 ? '' : 's'}
            {result.borrowersCreated > 0 && `, ${result.borrowersCreated} new borrower${result.borrowersCreated === 1 ? '' : 's'}`}
            {result.paymentsCreated > 0 && `, ${result.paymentsCreated} payment${result.paymentsCreated === 1 ? '' : 's'}`}.
          </div>
          <div className="modal-actions">
            <button className="btn btn-primary" onClick={onClose}>Done</button>
          </div>
        </>
      ) : (
        <form onSubmit={submit}>
          <ol className="steps">
            <li>
              <button type="button" className="link-btn" onClick={template}>Download the template</button>
              {' '}and fill one loan per row. The "How to fill" sheet explains each column.
            </li>
            <li>Choose the filled file and import. If any row has a problem, nothing is imported and every problem is listed.</li>
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
