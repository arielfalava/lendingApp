import { useEffect, useState } from 'react'
import { statusLabel } from '../util'

export function Modal({ title, onClose, children }) {
  useEffect(() => {
    const onKey = (e) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="Close">×</button>
        </div>
        {children}
      </div>
    </div>
  )
}

export function Field({ label, error, children, hint }) {
  return (
    <label className={`field ${error ? 'has-error' : ''}`}>
      <span className="field-label">{label}</span>
      {children}
      {error ? <span className="field-error">{error}</span> : hint ? <span className="field-hint">{hint}</span> : null}
    </label>
  )
}

export function StatusBadge({ status }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{statusLabel(status)}</span>
}

export function ErrorBanner({ error }) {
  if (!error) return null
  return <div className="banner banner-error">{error.message ?? String(error)}</div>
}

export function Empty({ children }) {
  return <div className="empty">{children}</div>
}

export function Loading() {
  return <div className="empty">Loading…</div>
}

/** Form state + submit handling that maps API validation errors onto fields. */
export function useForm(initial, submit) {
  const [values, setValues] = useState(initial)
  const [errors, setErrors] = useState({})
  const [formError, setFormError] = useState(null)
  const [saving, setSaving] = useState(false)

  const bind = (name) => ({
    name,
    value: values[name] ?? '',
    onChange: (e) => {
      setValues((v) => ({ ...v, [name]: e.target.value }))
      setErrors((er) => ({ ...er, [name]: undefined }))
    },
  })

  const onSubmit = async (e) => {
    e.preventDefault()
    setSaving(true)
    setFormError(null)
    try {
      await submit(values)
    } catch (err) {
      setErrors(err.fieldErrors ?? {})
      // Errors not tied to a visible field (e.g. business rules) go in the banner.
      const fieldNames = Object.keys(values)
      const other = Object.entries(err.fieldErrors ?? {}).filter(([k]) => !fieldNames.includes(k))
      if (!err.fieldErrors || Object.keys(err.fieldErrors).length === 0 || other.length) {
        setFormError(other.length ? new Error(other.map(([, m]) => m).join(' ')) : err)
      }
    } finally {
      setSaving(false)
    }
  }

  return { values, errors, formError, saving, bind, onSubmit }
}
