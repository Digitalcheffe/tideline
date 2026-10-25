import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { Panel, Problem, useAct, when } from './common'

interface TokenRow {
  id: number
  name: string
  prefix: string
  scope: 'read' | 'write'
  createdBy: string | null
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  revokedAt: string | null
  isActive: boolean
}

export function TokensSettings() {
  const tokens = useQuery({ queryKey: ['settings', 'tokens'], queryFn: () => get<TokenRow[]>('/tokens') })
  const [making, setMaking] = useState(false)
  const [secret, setSecret] = useState<{ name: string; token: string } | null>(null)
  const revoke = useAct()

  if (tokens.isLoading) return <Loading what="tokens" />
  if (tokens.error || !tokens.data) return <ErrorBox error={tokens.error} />

  return (
    <>
      {secret && (
        <Panel title={`Token for ${secret.name}`} onClose={() => setSecret(null)}>
          <p>Copy this now. It is shown once and only a fingerprint of it is kept, so it cannot be shown again.</p>
          <input readOnly value={secret.token} aria-label="New token" style={{ width: '100%' }} onFocus={(e) => e.currentTarget.select()} />
        </Panel>
      )}
      {making && (
        <NewToken
          onClose={() => setMaking(false)}
          onMade={(name, token) => {
            setMaking(false)
            setSecret({ name, token })
          }}
        />
      )}
      <Card
        title="API tokens"
        aside={
          <button type="button" className="btn primary" onClick={() => setMaking(true)}>
            Make a token
          </button>
        }
      >
        <p className="muted">
          For scripts and other tools. A read token can only look; a write token can also change lines. A token can never manage people, sessions or other tokens.
        </p>
        <Problem message={revoke.error} />
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Starts with</th>
                <th>Can</th>
                <th>Made</th>
                <th>Last used</th>
                <th>Expires</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {tokens.data.map((t) => (
                <tr key={t.id}>
                  <td>
                    {t.name} {t.revokedAt && <Badge tone="muted">revoked</Badge>}
                  </td>
                  <td>
                    <code>{t.prefix}…</code>
                  </td>
                  <td>{t.scope}</td>
                  <td>
                    {when(t.createdAt)}
                    {t.createdBy && <div className="muted">by {t.createdBy}</div>}
                  </td>
                  <td>{when(t.lastUsedAt)}</td>
                  <td>{t.expiresAt ? when(t.expiresAt) : 'never'}</td>
                  <td>
                    {t.revokedAt === null && (
                      <button
                        type="button"
                        className="btn"
                        disabled={revoke.busy}
                        onClick={() => {
                          if (window.confirm(`Revoke ${t.name}? Anything using it stops working at once.`)) void revoke.run(() => send('DELETE', `/tokens/${t.id}`))
                        }}
                      >
                        Revoke
                      </button>
                    )}
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

function NewToken({ onClose, onMade }: { onClose: () => void; onMade: (name: string, token: string) => void }) {
  const { run, error, busy } = useAct()
  const [name, setName] = useState('')
  const [scope, setScope] = useState('read')
  const [days, setDays] = useState('90')

  return (
    <Panel title="Make a token" onClose={onClose}>
      <form
        className="form"
        onSubmit={async (e) => {
          e.preventDefault()
          const made = await run(() => send<{ token: string }>('POST', '/tokens', { name: name.trim(), scope, expiresInDays: days === '' ? null : Number(days) }))
          if (made) onMade(name.trim(), made.token)
        }}
      >
        <label>
          What will use it
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Home dashboard" />
        </label>
        <label>
          It can
          <select value={scope} onChange={(e) => setScope(e.target.value)}>
            <option value="read">Read only</option>
            <option value="write">Read and change lines</option>
          </select>
        </label>
        <label>
          Expires after this many days (empty for never)
          <input type="number" min={1} max={3650} value={days} onChange={(e) => setDays(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || name.trim() === ''}>
            Make it
          </button>
        </div>
      </form>
    </Panel>
  )
}
