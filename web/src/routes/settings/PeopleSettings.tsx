import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { Panel, Problem, useAct, when } from './common'

interface Person {
  id: number
  email: string
  displayName: string
  role: 'owner' | 'member' | 'viewer'
  isActive: boolean
  mfaEnrolled: boolean
  hasPassword: boolean
  lastLoginAt: string | null
  lockedUntil: string | null
  activeSessions: number
}

const ROLES: [string, string][] = [
  ['owner', 'Owner: everything, including settings and people'],
  ['member', 'Member: sees and edits the money'],
  ['viewer', 'Viewer: sees the money, changes nothing'],
]

interface Link {
  setupUrl: string
  expiresAt: string
}

export function PeopleSettings() {
  const people = useQuery({ queryKey: ['settings', 'people'], queryFn: () => get<Person[]>('/users') })
  const [editing, setEditing] = useState<Person | 'new' | null>(null)
  const [link, setLink] = useState<{ who: string; link: Link } | null>(null)

  if (people.isLoading) return <Loading what="people" />
  if (people.error || !people.data) return <ErrorBox error={people.error} />

  return (
    <>
      {link && <SetupLink who={link.who} link={link.link} onClose={() => setLink(null)} />}
      {editing && (
        <PersonForm
          key={editing === 'new' ? 'new' : editing.id}
          person={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onLink={(who, l) => {
            setEditing(null)
            setLink({ who, link: l })
          }}
        />
      )}
      <Card
        title="People"
        aside={
          <button type="button" className="btn primary" onClick={() => setEditing('new')}>
            Invite someone
          </button>
        }
      >
        <p className="muted">
          Everyone signs in with a password and a code from an authenticator app. A new person gets a one-time setup link that lasts 15 minutes. If it runs out, make another.
        </p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Role</th>
                <th>Status</th>
                <th>Last signed in</th>
                <th className="num">Sessions</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {people.data.map((p) => (
                <tr key={p.id}>
                  <td>
                    {p.displayName}
                    <div className="muted">{p.email}</div>
                  </td>
                  <td>{p.role}</td>
                  <td>
                    {!p.isActive ? <Badge tone="muted">turned off</Badge> : !p.hasPassword || !p.mfaEnrolled ? <Badge tone="warn">setup not finished</Badge> : <Badge tone="good">active</Badge>}
                    {p.lockedUntil && new Date(p.lockedUntil) > new Date() && <Badge tone="bad">locked</Badge>}
                  </td>
                  <td>{when(p.lastLoginAt)}</td>
                  <td className="num">{p.activeSessions}</td>
                  <td>
                    <button type="button" className="btn" onClick={() => setEditing(p)}>
                      Manage
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </>
  )
}

/** The link is shown once, here. Whoever opens it first sets the password and the second step. */
function SetupLink({ who, link, onClose }: { who: string; link: Link; onClose: () => void }) {
  return (
    <Panel title={`Setup link for ${who}`} onClose={onClose}>
      <p>Send this link to {who} yourself. It works once and expires {when(link.expiresAt)}. It is not shown again.</p>
      <input readOnly value={link.setupUrl} aria-label="Setup link" style={{ width: '100%' }} onFocus={(e) => e.currentTarget.select()} />
    </Panel>
  )
}

function PersonForm({ person, onClose, onLink }: { person: Person | null; onClose: () => void; onLink: (who: string, link: Link) => void }) {
  const { run, error, busy } = useAct()
  const [email, setEmail] = useState(person?.email ?? '')
  const [name, setName] = useState(person?.displayName ?? '')
  const [role, setRole] = useState<string>(person?.role ?? 'member')

  const save = async () => {
    if (person) {
      if (await run(() => send('PATCH', `/users/${person.id}`, { displayName: name.trim(), role }))) onClose()
    } else {
      const made = await run(() => send<{ setupUrl: string; expiresAt: string }>('POST', '/users', { email: email.trim(), displayName: name.trim(), role }))
      if (made) onLink(name.trim(), made)
    }
  }
  const link = async (path: string, ask?: string) => {
    if (!person || (ask && !window.confirm(ask))) return
    const made = await run(() => send<Link>('POST', `/users/${person.id}/${path}`))
    if (made) onLink(person.displayName, made)
  }

  return (
    <Panel title={person ? `Manage ${person.displayName}` : 'Invite someone'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          void save()
        }}
      >
        <label>
          Email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} disabled={person !== null} />
        </label>
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Role
          <select value={role} onChange={(e) => setRole(e.target.value)}>
            {ROLES.map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || name.trim() === '' || email.trim() === ''}>
            {person ? 'Save' : 'Create and get the setup link'}
          </button>
        </div>
      </form>

      {person && (
        <div className="toolbar" style={{ marginTop: 16, flexWrap: 'wrap' }}>
          <button type="button" className="btn" disabled={busy} onClick={() => void link('setup-link')}>
            New setup link
          </button>
          <button
            type="button"
            className="btn"
            disabled={busy}
            onClick={() => void link('reset-mfa', `Reset ${person.displayName}'s second step? They are signed out everywhere and must set it up again with a new link.`)}
          >
            Reset second step
          </button>
          <button
            type="button"
            className="btn"
            disabled={busy || person.activeSessions === 0}
            onClick={() => {
              if (window.confirm(`Sign ${person.displayName} out everywhere?`)) void run(() => send('POST', `/users/${person.id}/sessions/revoke-all`)).then(onClose)
            }}
          >
            Sign out everywhere
          </button>
          <button
            type="button"
            className="btn"
            disabled={busy}
            onClick={() => {
              const next = !person.isActive
              if (window.confirm(next ? `Turn ${person.displayName} back on?` : `Turn ${person.displayName} off? They are signed out everywhere. Their history stays.`))
                void run(() => send('PATCH', `/users/${person.id}`, { isActive: next })).then(onClose)
            }}
          >
            {person.isActive ? 'Turn off' : 'Turn back on'}
          </button>
        </div>
      )}
    </Panel>
  )
}
