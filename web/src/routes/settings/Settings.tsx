import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import { useMe } from '../../auth'
import { Card } from '../../components'
import { AccountsSettings } from './AccountsSettings'
import { CategoriesSettings } from './CategoriesSettings'
import { RecurringSettings } from './RecurringSettings'
import { RulesSettings } from './RulesSettings'
import { PeopleSettings } from './PeopleSettings'
import { SessionsSettings } from './SessionsSettings'
import { TokensSettings } from './TokensSettings'
import { LogsSettings } from './LogsSettings'

const TABS = [
  { to: 'accounts', label: 'Money accounts' },
  { to: 'categories', label: 'Categories and tags' },
  { to: 'recurring', label: 'Recurring' },
  { to: 'rules', label: 'Rules' },
  { to: 'people', label: 'People' },
  { to: 'sessions', label: 'Sessions' },
  { to: 'tokens', label: 'API tokens' },
  { to: 'logs', label: 'Logs' },
]

/** Everything that sets the household up or guards it. Owners only: the server refuses everyone else too. */
export function Settings() {
  const me = useMe()
  if (me.user.role !== 'owner') {
    return (
      <>
        <h1>Settings</h1>
        <Card>
          <p className="muted">Only an owner can change settings.</p>
        </Card>
      </>
    )
  }
  return (
    <>
      <h1>Settings</h1>
      <nav className="subnav" aria-label="Settings">
        {TABS.map((t) => (
          <NavLink key={t.to} to={`/settings/${t.to}`} className={({ isActive }) => (isActive ? 'active' : '')}>
            {t.label}
          </NavLink>
        ))}
      </nav>
      <Routes>
        <Route index element={<Navigate to="accounts" replace />} />
        <Route path="accounts" element={<AccountsSettings />} />
        <Route path="categories" element={<CategoriesSettings />} />
        <Route path="recurring" element={<RecurringSettings />} />
        <Route path="rules" element={<RulesSettings />} />
        <Route path="people" element={<PeopleSettings />} />
        <Route path="sessions" element={<SessionsSettings />} />
        <Route path="tokens" element={<TokensSettings />} />
        <Route path="logs" element={<LogsSettings />} />
        <Route path="*" element={<Navigate to="accounts" replace />} />
      </Routes>
    </>
  )
}
