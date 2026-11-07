import { useEffect, useState } from 'react'
import { NavLink, Route, Routes } from 'react-router-dom'
import { api, setUnauthorizedHandler } from './api'
import BorrowerDetail from './pages/BorrowerDetail'
import Borrowers from './pages/Borrowers'
import Dashboard from './pages/Dashboard'
import History from './pages/History'
import LoanDetail from './pages/LoanDetail'
import Loans from './pages/Loans'
import Login from './pages/Login'

const NAV = [
  { to: '/', label: 'Dashboard', end: true },
  { to: '/borrowers', label: 'Borrowers' },
  { to: '/loans', label: 'Loans' },
  { to: '/history', label: 'History' },
]

export default function App() {
  // undefined = still checking the session, null = signed out, object = signed-in user
  const [user, setUser] = useState(undefined)

  useEffect(() => {
    setUnauthorizedHandler(() => setUser(null))
    api.me().then(setUser, () => setUser(null))
  }, [])

  const signOut = async () => {
    try { await api.logout() } finally { setUser(null) }
  }

  if (user === undefined) return null
  if (user === null) return <Login onLogin={setUser} />

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden>L</span>
          LendingApp
        </div>
        <nav>
          {NAV.map((n) => (
            <NavLink key={n.to} to={n.to} end={n.end} className={({ isActive }) => (isActive ? 'active' : '')}>
              {n.label}
            </NavLink>
          ))}
        </nav>
        <div className="account">
          <span className="account-email" title={user.email}>{user.email}</span>
          <button className="link-btn" onClick={signOut}>Sign out</button>
        </div>
      </aside>
      <main className="content">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/borrowers" element={<Borrowers />} />
          <Route path="/borrowers/:id" element={<BorrowerDetail />} />
          <Route path="/loans" element={<Loans />} />
          <Route path="/loans/:id" element={<LoanDetail />} />
          <Route path="/history" element={<History />} />
          <Route path="*" element={<p>Page not found.</p>} />
        </Routes>
      </main>
    </div>
  )
}
