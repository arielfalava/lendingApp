import { useEffect, useRef, useState } from 'react'
import { api } from '../api'

const GIS_SRC = 'https://accounts.google.com/gsi/client'

/** Loads Google Identity Services once and resolves with window.google.accounts.id. */
function loadGoogleIdentity() {
  if (window.google?.accounts?.id) return Promise.resolve(window.google.accounts.id)
  return new Promise((resolve, reject) => {
    let script = document.querySelector(`script[src="${GIS_SRC}"]`)
    if (!script) {
      script = Object.assign(document.createElement('script'), { src: GIS_SRC, async: true, defer: true })
      document.head.appendChild(script)
    }
    script.addEventListener('load', () => resolve(window.google.accounts.id))
    script.addEventListener('error', () => reject(new Error("Couldn't load Google sign-in. Check your internet connection.")))
  })
}

/**
 * "Continue with Google" signs in, or creates an account the first time a Gmail address is used.
 * Each account only ever sees its own borrowers and loans.
 * Email + password stays available for accounts that have a password (the original admin account).
 */
export default function Login({ onLogin }) {
  const buttonRef = useRef(null)
  const [googleState, setGoogleState] = useState('loading') // loading | ready | unavailable
  const [showPassword, setShowPassword] = useState(false)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState(null)
  const [busy, setBusy] = useState(false)

  const explain = (err) =>
    err.status === 429 ? 'Too many attempts. Wait a few minutes and try again.' : err.fieldErrors?.google ?? err.message

  useEffect(() => {
    let cancelled = false
    api.authConfig()
      .then(async ({ googleClientId }) => {
        if (!googleClientId) { setGoogleState('unavailable'); setShowPassword(true); return }
        const gis = await loadGoogleIdentity()
        if (cancelled) return
        gis.initialize({
          client_id: googleClientId,
          ux_mode: 'popup',
          callback: async ({ credential }) => {
            setBusy(true)
            setError(null)
            try { onLogin(await api.googleSignIn(credential)) } catch (err) { setError(explain(err)) } finally { setBusy(false) }
          },
        })
        const dark = window.matchMedia('(prefers-color-scheme: dark)').matches
        gis.renderButton(buttonRef.current, {
          type: 'standard', theme: dark ? 'filled_black' : 'outline', size: 'large',
          text: 'continue_with', shape: 'rectangular', logo_alignment: 'left', width: 320,
        })
        setGoogleState('ready')
      })
      .catch((err) => { if (!cancelled) { setGoogleState('unavailable'); setShowPassword(true); setError(err.message) } })
    return () => { cancelled = true }
  }, [onLogin])

  const submitPassword = async (e) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onLogin(await api.login(email, password))
    } catch (err) {
      setError(explain(err))
      setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="login-page">
      <div className="login-card">
        <div className="brand login-brand">
          <span className="brand-mark" aria-hidden>L</span>
          LendingApp
        </div>
        <h1>Sign in</h1>
        <p className="muted login-lead">
          Use your Gmail account. New here? Continuing with Google creates your account, and your borrowers and loans are visible only to you.
        </p>
        {error && <div className="banner banner-error" role="alert">{error}</div>}

        <div className="google-slot" aria-busy={googleState === 'loading' || busy}>
          <div ref={buttonRef} hidden={googleState !== 'ready'} />
          {googleState === 'loading' && <div className="muted">Loading Google sign-in…</div>}
          {googleState === 'unavailable' && !error && (
            <div className="muted">Google sign-in isn't set up on this server yet.</div>
          )}
          {busy && <div className="muted">Signing in…</div>}
        </div>

        {googleState === 'ready' && !showPassword && (
          <p className="login-switch muted">
            <button type="button" className="link-btn" onClick={() => setShowPassword(true)}>Sign in with email and password instead</button>
          </p>
        )}

        {showPassword && (
          <form onSubmit={submitPassword} className="password-signin">
            {googleState === 'ready' && <div className="divider"><span>or</span></div>}
            <label className="field">
              <span className="field-label">Email</span>
              <input type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />
            </label>
            <label className="field">
              <span className="field-label">Password</span>
              <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
            </label>
            <button type="submit" className="btn btn-primary login-submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
          </form>
        )}
      </div>
    </main>
  )
}
