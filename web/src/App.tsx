import { useEffect, useState } from 'react'
import { Link, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { PeriodPicker, usePeriod } from './period'
import { monthLabel, wholeMonth, wholeYear } from './format'
import { Overview } from './routes/Overview'
import { Accounts } from './routes/Accounts'
import { Cards } from './routes/Cards'
import { Bills } from './routes/Bills'
import { TypeScreen } from './routes/TypeScreen'
import { Income } from './routes/Income'
import { Lines } from './routes/Lines'
import { Coming } from './routes/Coming'

const GROUPS: { title: string; items: { to: string; label: string }[] }[] = [
  {
    title: 'Money',
    items: [
      { to: '/', label: 'Overview' },
      { to: '/accounts', label: 'Accounts' },
      { to: '/cards', label: 'Cards' },
      { to: '/bills', label: 'Bills' },
      { to: '/required', label: 'Required' },
      { to: '/extra', label: 'Extra' },
      { to: '/income', label: 'Income' },
    ],
  },
  {
    title: 'Looking ahead',
    items: [
      { to: '/savings', label: 'Savings' },
      { to: '/planning', label: 'Planning' },
      { to: '/budget', label: 'Budget' },
      { to: '/retirement', label: 'Retirement' },
    ],
  },
  { title: 'Detail', items: [{ to: '/lines', label: 'Ledger' }] },
]

// screens where the period picker does nothing, so it is not shown
const NO_PERIOD = ['/savings', '/planning', '/budget', '/retirement', '/settings', '/recommendations']

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
  const [menuOpen, setMenuOpen] = useState(false)
  const showPeriod = !NO_PERIOD.includes(location.pathname)

  // the menu closes when you pick a screen
  useEffect(() => setMenuOpen(false), [location.pathname])

  const link = (to: string) => (to === '/settings' ? to : `${to}?from=${period.from}&to=${period.to}`)
  const periodLabel = wholeMonth(period.from, period.to)
    ? monthLabel(period.from.slice(0, 7))
    : wholeYear(period.from, period.to)
      ? String(period.year)
      : `${period.from} to ${period.to}`

  return (
    <div className="shell">
      <header className="mobilebar">
        <button type="button" className="hamburger" aria-label="Menu" aria-expanded={menuOpen} onClick={() => setMenuOpen(!menuOpen)}>
          <span />
          <span />
          <span />
        </button>
        <Link to="/" className="brand">
          <Logo /> Tideline
        </Link>
        {showPeriod && <span className="muted mobile-period">{periodLabel}</span>}
      </header>

      {menuOpen && <div className="menu-scrim" onClick={() => setMenuOpen(false)} />}

      <aside className={`sidebar ${menuOpen ? 'open' : ''}`} aria-label="Menu">
        <Link to="/" className="brand desktop-only">
          <Logo /> Tideline
        </Link>

        {showPeriod && <PeriodPicker period={period} />}

        <nav aria-label="Screens">
          {GROUPS.map((g) => (
            <div className="nav-group" key={g.title}>
              <div className="nav-title">{g.title}</div>
              {g.items.map((n) => (
                <NavLink key={n.to} to={link(n.to)} end={n.to === '/'} className={({ isActive }) => (isActive ? 'active' : '')}>
                  {n.label}
                </NavLink>
              ))}
            </div>
          ))}
        </nav>

        <div className="nav-foot">
          <NavLink to="/settings" className={({ isActive }) => (isActive ? 'active' : '')}>
            Settings
          </NavLink>
        </div>
      </aside>

      <div className="content">
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
      </div>
    </div>
  )
}
