import { NavLink, Route, Routes } from 'react-router-dom'
import BorrowerDetail from './pages/BorrowerDetail'
import Borrowers from './pages/Borrowers'
import Dashboard from './pages/Dashboard'
import History from './pages/History'
import LoanDetail from './pages/LoanDetail'
import Loans from './pages/Loans'

const NAV = [
  { to: '/', label: 'Dashboard', end: true },
  { to: '/borrowers', label: 'Borrowers' },
  { to: '/loans', label: 'Loans' },
  { to: '/history', label: 'History' },
]

export default function App() {
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
