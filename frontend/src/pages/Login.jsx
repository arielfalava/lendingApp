import { useState } from 'react'
import { api } from '../api'

/** Sign-in and Create account share one card; each account only ever sees its own borrowers and loans. */
export default function Login({ onLogin }) {
  const [mode, setMode] = useState('signin') // 'signin' | 'register'
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState(null)
  const [fieldErrors, setFieldErrors] = useState({})
  const [busy, setBusy] = useState(false)
  const registering = mode === 'register'

  const switchMode = (next) => {
    setMode(next)
    setError(null)
    setFieldErrors({})
    setPassword('')
    setConfirm('')
  }

  const submit = async (e) => {
    e.preventDefault()
    setError(null)
    setFieldErrors({})
    if (registering && password !== confirm) {
      setFieldErrors({ confirm: "Passwords don't match." })
      return
    }
    setBusy(true)
    try {
      onLogin(registering ? await api.register(email, password) : await api.login(email, password))
    } catch (err) {
      if (err.status === 429) setError('Too many attempts. Wait a few minutes and try again.')
      else if (Object.keys(err.fieldErrors ?? {}).length) setFieldErrors(err.fieldErrors)
      else setError(err.message)
      if (!registering) setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login-page">
      <form className="login-card" onSubmit={submit} noValidate>
        <div className="brand login-brand">
          <span className="brand-mark" aria-hidden>L</span>
          LendingApp
        </div>
        <h1>{registering ? 'Create account' : 'Sign in'}</h1>
        {registering && <p className="muted login-lead">Use your Gmail address. Your borrowers and loans are visible only to you.</p>}
        {error && <div className="banner banner-error" role="alert">{error}</div>}

        <label className={`field ${fieldErrors.email ? 'has-error' : ''}`}>
          <span className="field-label">{registering ? 'Gmail address' : 'Email'}</span>
          <input
            type="email"
            autoComplete={registering ? 'email' : 'username'}
            placeholder={registering ? 'name@gmail.com' : undefined}
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoFocus
            required
          />
          {fieldErrors.email && <span className="field-error">{fieldErrors.email}</span>}
        </label>

        <label className={`field ${fieldErrors.password ? 'has-error' : ''}`}>
          <span className="field-label">Password</span>
          <input
            type="password"
            autoComplete={registering ? 'new-password' : 'current-password'}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
          {fieldErrors.password
            ? <span className="field-error">{fieldErrors.password}</span>
            : registering && <span className="field-hint">At least 8 characters, with a letter and a number.</span>}
        </label>

        {registering && (
          <label className={`field ${fieldErrors.confirm ? 'has-error' : ''}`}>
            <span className="field-label">Confirm password</span>
            <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
            {fieldErrors.confirm && <span className="field-error">{fieldErrors.confirm}</span>}
          </label>
        )}

        <button type="submit" className="btn btn-primary login-submit" disabled={busy}>
          {busy ? (registering ? 'Creating account…' : 'Signing in…') : registering ? 'Create account' : 'Sign in'}
        </button>

        <p className="login-switch muted">
          {registering ? 'Already have an account?' : 'New here?'}{' '}
          <button type="button" className="link-btn" onClick={() => switchMode(registering ? 'signin' : 'register')}>
            {registering ? 'Sign in' : 'Create an account'}
          </button>
        </p>
      </form>
    </main>
  )
}
