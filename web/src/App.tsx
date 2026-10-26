import { lazy, Suspense, useEffect, useState } from 'react'
import { Link, NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { PeriodPicker, usePeriod } from './period'
import { monthLabel, wholeMonth, wholeYear } from './format'
import { AuthGate, useMe, useSignOut } from './auth'
import { Coming } from './routes/Coming'
import { ThemeToggle } from './theme'
import { Avatar, Icon } from './icons'

// each screen loads when it is first opened, so the chart library is only fetched for the screens that draw charts
const Overview = lazy(() => import('./routes/Overview').then((m) => ({ default: m.Overview })))
const Accounts = lazy(() => import('./routes/Accounts').then((m) => ({ default: m.Accounts })))
const Cards = lazy(() => import('./routes/Cards').then((m) => ({ default: m.Cards })))
const Bills = lazy(() => import('./routes/Bills').then((m) => ({ default: m.Bills })))
const TypeScreen = lazy(() => import('./routes/TypeScreen').then((m) => ({ default: m.TypeScreen })))
const Income = lazy(() => import('./routes/Income').then((m) => ({ default: m.Income })))
const Lines = lazy(() => import('./routes/Lines').then((m) => ({ default: m.Lines })))
const Settings = lazy(() => import('./routes/settings/Settings').then((m) => ({ default: m.Settings })))
const Budget = lazy(() => import('./routes/Budget').then((m) => ({ default: m.Budget })))
const Planning = lazy(() => import('./routes/Planning').then((m) => ({ default: m.Planning })))
const Retirement = lazy(() => import('./routes/Retirement').then((m) => ({ default: m.Retirement })))
const Recommendations = lazy(() => import('./routes/Recommendations').then((m) => ({ default: m.Recommendations })))
const Help = lazy(() => import('./routes/Help').then((m) => ({ default: m.Help })))
const Account = lazy(() => import('./routes/Account').then((m) => ({ default: m.Account })))
const Assets = lazy(() => import('./routes/Assets').then((m) => ({ default: m.Assets })))
const Savings = lazy(() => import('./routes/Savings').then((m) => ({ default: m.Savings })))

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
      { to: '/assets', label: 'Assets' },
      { to: '/planning', label: 'Planning' },
      { to: '/budget', label: 'Budget' },
      { to: '/retirement', label: 'Retirement' },
      { to: '/recommendations', label: 'Recommendations' },
    ],
  },
  { title: 'Detail', items: [{ to: '/lines', label: 'Ledger' }] },
]

// screens where the period picker does nothing, so it is not shown
const NO_PERIOD = ['/help', '/account', '/savings', '/assets', '/planning', '/retirement', '/settings', '/recommendations']

function Logo() {
  return (
    <svg viewBox="0 0 32 32" aria-hidden>
      <path d="M2 20c4-6 8-6 12 0s8 6 16-2" fill="none" stroke="var(--accent)" strokeWidth="3" strokeLinecap="round" />
      <path d="M2 12c4-6 8-6 12 0s8 6 16-2" fill="none" stroke="var(--accent)" strokeWidth="3" strokeLinecap="round" opacity="0.45" />
    </svg>
  )
}

export default function App() {
  return (
    <AuthGate>
      <Shell />
    </AuthGate>
  )
}

function Shell() {
  const me = useMe()
  const signOut = useSignOut()
  const period = usePeriod()
  const location = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const showPeriod = !NO_PERIOD.some((p) => location.pathname === p || location.pathname.startsWith(`${p}/`))

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
      {me.authDisabled && (
        <div className="devbanner" role="alert">
          Sign-in is switched off for testing. Everyone on this computer is the owner. Never use this setting with real data on a shared machine.
        </div>
      )}
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
          <NavLink to="/help" className={({ isActive }) => `with-icon ${isActive ? 'active' : ''}`}>
            <Icon name="help" size={18} /> Help
          </NavLink>
          {me.user.role === 'owner' && (
            <NavLink to="/settings" className={({ isActive }) => (isActive ? 'active' : '')}>
              Settings
            </NavLink>
          )}
          <ThemeToggle />
          <div className="whoami">
            {me.authDisabled ? (
              <span className="whoami-who">
                <Avatar name={me.user.displayName} size={34} />
                <span className="whoami-text">
                  <strong>{me.user.displayName}</strong>
                  <span className="muted">{me.user.role}</span>
                </span>
              </span>
            ) : (
              <NavLink to="/account" className={({ isActive }) => `whoami-who ${isActive ? 'active' : ''}`} title="Your account: name, password, authenticator, devices">
                <Avatar name={me.user.displayName} size={34} />
                <span className="whoami-text">
                  <strong>{me.user.displayName}</strong>
                  <span className="muted">{me.user.role}</span>
                </span>
              </NavLink>
            )}
            {!me.authDisabled && (
              <button className="icon-btn" type="button" onClick={() => signOut()} title="Sign out" aria-label="Sign out">
                <Icon name="logout" />
              </button>
            )}
          </div>
        </div>
      </aside>

      <div className="content">
        <main>
          <Suspense fallback={<p className="muted">Loading…</p>}>
          <Routes>
            <Route path="/" element={<Overview period={period} />} />
            <Route path="/accounts" element={<Accounts period={period} />} />
            <Route path="/cards" element={<Cards period={period} />} />
            <Route path="/bills" element={<Bills period={period} />} />
            <Route path="/required" element={<TypeScreen type="required" title="Required" period={period} />} />
            <Route path="/extra" element={<TypeScreen type="extra" title="Extra" period={period} />} />
            <Route path="/income" element={<Income period={period} />} />
            <Route path="/lines" element={<Lines />} />
            <Route path="/savings" element={<Savings />} />
            <Route path="/assets" element={<Assets />} />
            <Route path="/account" element={<Account />} />
            <Route path="/help" element={<Help />} />
            <Route path="/help/:topic" element={<Help />} />
            <Route path="/planning" element={<Planning />} />
            <Route path="/budget" element={<Budget period={period} />} />
            <Route path="/retirement" element={<Retirement />} />
            <Route path="/recommendations" element={<Recommendations />} />
            <Route path="/settings/*" element={<Settings />} />
            <Route path="*" element={<Coming title="Not found" phase="" />} />
          </Routes>
          </Suspense>
        </main>
      </div>
    </div>
  )
}
