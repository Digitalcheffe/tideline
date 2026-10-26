import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { Panel, Problem, useAct, when } from './common'

interface Channel {
  id: number
  kind: 'webhook' | 'email'
  name: string
  isActive: boolean
  summary: string
}

interface Pref {
  kind: string
  description: string
  mode: 'immediate' | 'summary' | 'off'
  defaultMode: string
}

interface Status {
  quietStart: string
  quietEnd: string
  summaryDay: number
  summaryTime: string
  evaluateAt: string
  lastEvaluationDay: string | null
  recent: { at: string; channel: string; status: string; error: string | null; title: string }[]
}

interface Summary {
  month: string
  title: string
  body: string
  needsYou: number
}

interface SettingsData {
  cardInterestCategoryId: number | null
  debtWords: string
}

export function NotificationsSettings() {
  const channels = useQuery({ queryKey: ['settings', 'channels'], queryFn: () => get<Channel[]>('/notify/channels') })
  const prefs = useQuery({ queryKey: ['settings', 'prefs'], queryFn: () => get<Pref[]>('/notify/prefs') })
  const status = useQuery({ queryKey: ['settings', 'notify-status'], queryFn: () => get<Status>('/notify/status') })
  const [adding, setAdding] = useState(false)

  if (channels.isLoading || prefs.isLoading || status.isLoading) return <Loading what="notifications" />
  if (channels.error || prefs.error || status.error || !channels.data || !prefs.data || !status.data) return <ErrorBox error={channels.error ?? prefs.error ?? status.error} />

  return (
    <>
      {adding && <ChannelForm onClose={() => setAdding(false)} />}
      <Card
        title="Where messages go"
        aside={
          <button type="button" className="btn primary" onClick={() => setAdding(true)}>
            Add a channel
          </button>
        }
      >
        <p className="muted">
          A webhook posts a message to something you run, such as an automation that puts it in a chat room. Email goes by SMTP. The addresses and passwords are stored sealed
          and are never shown again.
        </p>
        {channels.data.length === 0 ? (
          <p className="muted">No channels yet, so nothing is sent. Flags still show on the Overview.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <tbody>
                {channels.data.map((c) => (
                  <ChannelRow key={c.id} channel={c} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Prefs prefs={prefs.data} />
      <Counts />
      <Schedule status={status.data} />
    </>
  )
}

function ChannelRow({ channel }: { channel: Channel }) {
  const { run, error, busy } = useAct()
  const [result, setResult] = useState<{ ok: boolean; error: string | null } | null>(null)

  return (
    <tr>
      <td>
        {channel.name} <span className="muted">({channel.kind})</span> {!channel.isActive && <Badge tone="muted">off</Badge>}
        <div className="muted">{channel.summary}</div>
        {result && (
          <div role="status">
            {result.ok ? <Badge tone="good">Test message sent</Badge> : <Badge tone="bad">Failed</Badge>} {result.error}
          </div>
        )}
        <Problem message={error} />
      </td>
      <td>
        <button type="button" className="btn" disabled={busy} onClick={async () => setResult((await run(() => send<{ ok: boolean; error: string | null }>('POST', '/notify/test', { channelId: channel.id }))) ?? null)}>
          Send a test
        </button>{' '}
        <button type="button" className="btn" disabled={busy} onClick={() => void run(() => send('PATCH', `/notify/channels/${channel.id}`, { isActive: !channel.isActive }))}>
          {channel.isActive ? 'Turn off' : 'Turn on'}
        </button>{' '}
        <button
          type="button"
          className="btn"
          disabled={busy}
          onClick={() => {
            if (window.confirm(`Remove ${channel.name}? Its history of what was sent goes too.`)) void run(() => send('DELETE', `/notify/channels/${channel.id}`))
          }}
        >
          Remove
        </button>
      </td>
    </tr>
  )
}

function ChannelForm({ onClose }: { onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [kind, setKind] = useState<'webhook' | 'email'>('webhook')
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [secret, setSecret] = useState('')
  const [host, setHost] = useState('')
  const [port, setPort] = useState('587')
  const [ssl, setSsl] = useState(true)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')

  const valid = name.trim() !== '' && (kind === 'webhook' ? url.trim() !== '' : host.trim() !== '' && from.includes('@') && to.includes('@'))
  const config = kind === 'webhook' ? { url: url.trim(), secret: secret || null } : { host: host.trim(), port: Number(port), useSsl: ssl, from: from.trim(), to: to.trim(), username: user || null, password: password || null }

  return (
    <Panel title="Add a channel" onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (valid) void run(() => send('POST', '/notify/channels', { kind, name: name.trim(), config }))
        }}
      >
        <label>
          Kind
          <select value={kind} onChange={(e) => setKind(e.target.value as 'webhook' | 'email')}>
            <option value="webhook">Webhook</option>
            <option value="email">Email</option>
          </select>
        </label>
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Chat" />
        </label>
        {kind === 'webhook' ? (
          <>
            <label>
              Webhook address
              <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://hooks.example.test/webhook/tideline" autoComplete="off" />
            </label>
            <label>
              Shared secret (sent in a header, optional)
              <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="new-password" />
            </label>
          </>
        ) : (
          <>
            <label>
              Mail server
              <input value={host} onChange={(e) => setHost(e.target.value)} />
            </label>
            <label>
              Port
              <input type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} />
            </label>
            <label>
              <span>
                <input type="checkbox" checked={ssl} onChange={(e) => setSsl(e.target.checked)} /> Use an encrypted connection
              </span>
            </label>
            <label>
              From address
              <input value={from} onChange={(e) => setFrom(e.target.value)} />
            </label>
            <label>
              To address
              <input value={to} onChange={(e) => setTo(e.target.value)} />
            </label>
            <label>
              User name (if the server asks)
              <input value={user} onChange={(e) => setUser(e.target.value)} autoComplete="off" />
            </label>
            <label>
              Password
              <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
            </label>
          </>
        )}
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || !valid}>
            Save
          </button>
        </div>
      </form>
    </Panel>
  )
}

function Prefs({ prefs }: { prefs: Pref[] }) {
  const { run, error, busy } = useAct()
  return (
    <Card title="What gets sent, and how">
      <p className="muted">
        At once means a message as soon as it is found (but not during quiet hours). In the summary means it waits for the monthly message. Off means it only shows on the
        Overview.
      </p>
      <Problem message={error} />
      <div className="table-wrap">
        <table>
          <tbody>
            {prefs.map((p) => (
              <tr key={p.kind}>
                <td>
                  {p.description}
                  <div className="muted">{p.kind}</div>
                </td>
                <td>
                  <select
                    aria-label={p.description}
                    value={p.mode}
                    disabled={busy}
                    onChange={(e) => void run(() => send('PUT', '/notify/prefs', { modes: { [p.kind]: e.target.value } }))}
                  >
                    <option value="immediate">At once</option>
                    <option value="summary">In the summary</option>
                    <option value="off">Off</option>
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  )
}

function Counts() {
  const settings = useQuery({ queryKey: ['settings', 'app'], queryFn: () => get<SettingsData>('/settings') })
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<{ id: number; name: string }[]>('/categories') })
  const { run, error, busy } = useAct()
  const [words, setWords] = useState<string | null>(null)

  if (!settings.data || !categories.data) return null
  const shown = words ?? settings.data.debtWords

  return (
    <Card title="What counts">
      <div className="form">
        <label>
          The category that means interest or fees on a card
          <select
            value={settings.data.cardInterestCategoryId ?? ''}
            disabled={busy}
            onChange={(e) => void run(() => send('PUT', '/settings', e.target.value === '' ? { clearCardInterestCategory: true } : { cardInterestCategoryId: Number(e.target.value) }))}
          >
            <option value="">None chosen</option>
            {categories.data.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Words that make a payment look like a new loan (separated by commas)
          <textarea value={shown} onChange={(e) => setWords(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button type="button" className="btn" disabled={busy || words === null} onClick={() => void run(() => send('PUT', '/settings', { debtWords: words })).then(() => setWords(null))}>
            Save the words
          </button>
        </div>
      </div>
    </Card>
  )
}

function Schedule({ status }: { status: Status }) {
  const evaluate = useAct()
  const summary = useAct()
  const [preview, setPreview] = useState<Summary | null>(null)
  const [sent, setSent] = useState<string | null>(null)

  return (
    <Card title="Schedule and history">
      <ul>
        <li>Everything is checked after each import and every day at {status.evaluateAt}.</li>
        <li>
          Quiet hours: {status.quietStart} to {status.quietEnd}. Urgent messages wait until they end.
        </li>
        <li>
          The monthly summary goes out on day {status.summaryDay} at {status.summaryTime}. (These times are set in the app&rsquo;s configuration: Notify__SummaryDay, Notify__SummaryTime,
          Notify__QuietStart, Notify__QuietEnd.)
        </li>
        <li>Last daily check: {status.lastEvaluationDay ?? 'not yet'}.</li>
      </ul>
      <div className="toolbar">
        <button type="button" className="btn" disabled={evaluate.busy} onClick={() => void evaluate.run(() => send('POST', '/notify/evaluate'))}>
          Check everything now
        </button>
        <button type="button" className="btn" disabled={summary.busy} onClick={async () => setPreview((await summary.run(() => get<Summary>('/notify/summary'))) ?? null)}>
          Preview last month&rsquo;s summary
        </button>
        <button
          type="button"
          className="btn"
          disabled={summary.busy}
          onClick={async () => {
            const done = await summary.run(() => send<{ delivered: number; month: string }>('POST', '/notify/summary/send', {}))
            if (done) setSent(`Sent the ${done.month} summary to ${done.delivered} channel${done.delivered === 1 ? '' : 's'}.`)
          }}
        >
          Send it now
        </button>
      </div>
      <Problem message={evaluate.error ?? summary.error} />
      {sent && <p role="status">{sent}</p>}
      {preview && (
        <pre className="preview">
          {preview.title}
          {'\n'}
          {preview.body}
        </pre>
      )}

      <h3>Recently sent</h3>
      {status.recent.length === 0 ? (
        <p className="muted">Nothing yet.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>When</th>
                <th>To</th>
                <th>What</th>
                <th>Result</th>
              </tr>
            </thead>
            <tbody>
              {status.recent.map((r, i) => (
                <tr key={i}>
                  <td>{when(r.at)}</td>
                  <td>{r.channel}</td>
                  <td>{r.title}</td>
                  <td>{r.status === 'sent' ? <Badge tone="good">sent</Badge> : <Badge tone="bad">failed</Badge>} {r.error}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}
