import { NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { PeriodPicker, usePeriod } from './period'
import { Overview } from './routes/Overview'
import { Accounts } from './routes/Accounts'
import { Cards } from './routes/Cards'
import { Bills } from './routes/Bills'
import { TypeScreen } from './routes/TypeScreen'
import { Income } from './routes/Income'
import { Lines } from './routes/Lines'
import { Coming } from './routes/Coming'

const NAV: { to: string; label: string }[] = [
  { to: '/', label: 'Overview' },
  { to: '/accounts', label: 'Accounts' },
  { to: '/cards', label: 'Cards' },
  { to: '/bills', label: 'Bills' },
  { to: '/required', label: 'Required' },
  { to: '/extra', label: 'Extra' },
  { to: '/income', label: 'Income' },
  { to: '/savings', label: 'Savings' },
  { to: '/planning', label: 'Planning' },
  { to: '/budget', label: 'Budget' },
  { to: '/retirement', label: 'Retirement' },
  { to: '/lines', label: 'Lines' },
  { to: '/settings', label: 'Settings' },
]

function Logo() {
  return (
    <svg viewBox="0 0 32 32" aria-hidden>
      <path d="M2 20c4-6 8-6 12 0s8 6 16-2" fill="none" stroke="var(--accent)" strokeWidth="3" strokeLinecap="round" />
      <path d="M2 12c4-6 8-6 12 0s8 6 16-2" fill="none" stroke="var(--accent)" strokeWidth="3" strokeLinecap="round" opacity="0.45" />
    </svg>
  )
}

export default function App() {
  const period = usePeriod()
  const location = useLocation()
  // /lines carries its own filters in the URL, so the picker would fight with them there
  const showPeriod = location.pathname !== '/lines' && !['/savings', '/planning', '/budget', '/retirement', '/settings'].includes(location.pathname)

  return (
    <>
      <header className="topbar">
        <div className="topbar-inner">
          <NavLink to="/" className="brand">
            <Logo /> Tideline
          </NavLink>
          <nav className="nav" aria-label="Screens">
            {NAV.map((n) => (
              <NavLink key={n.to} to={n.to + (n.to === '/lines' ? '' : location.search)} end={n.to === '/'} className={({ isActive }) => (isActive ? 'active' : '')}>
                {n.label}
              </NavLink>
            ))}
          </nav>
          {showPeriod && <PeriodPicker period={period} />}
        </div>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Overview period={period} />} />
          <Route path="/accounts" element={<Accounts period={period} />} />
          <Route path="/cards" element={<Cards period={period} />} />
          <Route path="/bills" element={<Bills period={period} />} />
          <Route path="/required" element={<TypeScreen type="required" title="Required" period={period} />} />
          <Route path="/extra" element={<TypeScreen type="extra" title="Extra" period={period} />} />
          <Route path="/income" element={<Income period={period} />} />
          <Route path="/lines" element={<Lines />} />
          <Route path="/savings" element={<Coming title="Savings" phase="Phase 4" />} />
          <Route path="/planning" element={<Coming title="Planning" phase="Phase 4" />} />
          <Route path="/budget" element={<Coming title="Budget" phase="Phase 4" />} />
          <Route path="/retirement" element={<Coming title="Retirement" phase="Phase 4" />} />
          <Route path="/recommendations" element={<Coming title="Recommendations" phase="a later phase" />} />
          <Route path="/settings" element={<Coming title="Settings" phase="Phase 3" />} />
          <Route path="*" element={<Coming title="Not found" phase="" />} />
        </Routes>
      </main>
    </>
  )
}
