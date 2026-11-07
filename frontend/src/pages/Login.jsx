import { useState } from 'react'
import { api } from '../api'

export default function Login({ onLogin }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onLogin(await api.login(email, password))
    } catch (err) {
      setError(err.status === 429 ? 'Too many attempts. Wait a few minutes and try again.' : err.message)
      setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login-page">
      <form className="login-card" onSubmit={submit}>
        <div className="brand login-brand">
          <span className="brand-mark" aria-hidden>L</span>
          LendingApp
        </div>
        <h1>Sign in</h1>
        {error && <div className="banner banner-error" role="alert">{error}</div>}
        <label className="field">
          <span className="field-label">Email</span>
          <input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} autoFocus required />
        </label>
        <label className="field">
          <span className="field-label">Password</span>
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        <button type="submit" className="btn btn-primary login-submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      </form>
    </main>
  )
}
