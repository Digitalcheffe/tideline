import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { Problem, useAct, when } from './common'

interface SessionRow {
  id: number
  userId: number
  userName: string
  userEmail: string
  createdAt: string
  lastSeenAt: string
  expiresAt: string
  ip: string | null
  userAgent: string | null
  secondStepPassed: boolean
  isCurrent: boolean
}

/** A readable name for the browser, from the user-agent text the browser sent. */
function device(agent: string | null): string {
  if (!agent) return 'Unknown device'
  const browser = /Edg\//.test(agent) ? 'Edge' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome' : /Safari\//.test(agent) ? 'Safari' : 'Browser'
  const os = /Windows/.test(agent) ? 'Windows' : /iPhone|iPad/.test(agent) ? 'iOS' : /Android/.test(agent) ? 'Android' : /Mac OS/.test(agent) ? 'macOS' : /Linux/.test(agent) ? 'Linux' : ''
  return os ? `${browser} on ${os}` : browser
}

export function SessionsSettings() {
  const sessions = useQuery({ queryKey: ['settings', 'sessions'], queryFn: () => get<SessionRow[]>('/sessions') })
  const { run, error, busy } = useAct()

  if (sessions.isLoading) return <Loading what="sessions" />
  if (sessions.error || !sessions.data) return <ErrorBox error={sessions.error} />

  return (
    <Card title="Signed-in sessions">
      <p className="muted">Every browser that is signed in right now. Sign one out if you do not recognise it or a phone is lost.</p>
      <Problem message={error} />
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Who</th>
              <th>Device</th>
              <th>Address</th>
              <th>Signed in</th>
              <th>Last seen</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {sessions.data.map((s) => (
              <tr key={s.id}>
                <td>
                  {s.userName}
                  <div className="muted">{s.userEmail}</div>
                </td>
                <td>
                  {device(s.userAgent)} {s.isCurrent && <Badge tone="info">this one</Badge>} {!s.secondStepPassed && <Badge tone="warn">second step not done</Badge>}
                </td>
                <td>{s.ip ?? ''}</td>
                <td>{when(s.createdAt)}</td>
                <td>{when(s.lastSeenAt)}</td>
                <td>
                  <button
                    type="button"
                    className="btn"
                    disabled={busy}
                    onClick={() => {
                      if (!s.isCurrent || window.confirm('This is the session you are using. You will be signed out.')) void run(() => send('DELETE', `/sessions/${s.id}`))
                    }}
                  >
                    Sign out
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  )
}
