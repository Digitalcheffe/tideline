import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { dollars } from '../../format'
import { Problem, useAct, when } from './common'

interface Status {
  connected: boolean
  lastSyncAt: string | null
  lastError: string | null
  requestsToday: number
  requestLimit: number
  everyHours: number
  matched: { accountId: number; name: string; simplefinAccountId: string; takesOverOn: string | null }[]
}

interface Remote {
  id: string
  name: string
  organization: string
  balanceCents: number
  mappedAccountId: number | null
  mappedAccountName: string | null
  suggestedAccountId: number | null
}

interface Ours {
  id: number
  name: string
  kind: string
}

interface SyncReport {
  accounts: { accountId: number; accountName: string; fetched: number; pendingSkipped: number; beforeCutover: number; inserted: number; existing: number; toReview: number; balanceMatches: boolean | null; error: string | null }[]
  errors: string[]
  requests: number
}

interface DryLine {
  postedOn: string
  description: string
  amountCents: number
  ledgerDescription: string | null
  difference: string
}

interface DryRun {
  accountName: string
  from: string
  to: string
  simpleFinRows: number
  matched: number
  sameMeaning: number
  different: DryLine[]
  onlyInSimpleFin: DryLine[]
  onlyInLedger: DryLine[]
}

const DISCONNECT_WARNING =
  'Disconnect SimpleFIN?\n\nThis only makes Tideline forget the connection. It does not cancel it at SimpleFIN. To stop access for good, also revoke it at SimpleFIN Bridge (disable the token or connection there).\n\nLines already imported stay. You will need a new setup token to connect again.'

/** The second door in (docs/05-import.md, "SimpleFIN"): connect once, match accounts, try a month beside the CSV, then let it run. */
export function SimpleFinPanel() {
  const status = useQuery({ queryKey: ['settings', 'simplefin'], queryFn: () => get<Status>('/imports/simplefin') })
  if (status.isLoading) return <Loading what="SimpleFIN" />
  if (status.error || !status.data) return <ErrorBox error={status.error} />
  return status.data.connected ? <Connected status={status.data} /> : <Connect />
}

function Connect() {
  const { run, error, busy } = useAct()
  const [token, setToken] = useState('')
  return (
    <Card title="SimpleFIN">
      <p className="muted">
        SimpleFIN Bridge is a separate service that reads your accounts at the bank, read only. Tideline asks it for new lines every few hours, so files are not needed. Tideline can
        look at your accounts through it but cannot move money, and it never sees your bank login.
      </p>
      <ol className="steps">
        <li>Go to SimpleFIN Bridge (simplefin.org) and sign in, or make an account there.</li>
        <li>Link your bank at the Bridge. You sign in to the bank on the Bridge&rsquo;s own page, not in Tideline.</li>
        <li>At the Bridge, make a new setup token for Tideline and copy the whole thing.</li>
        <li>Paste it below and press Connect straight away. A token works one time only, so if you press Connect and it fails, make a new token.</li>
        <li>Then match each SimpleFIN account to one of yours, pick the day SimpleFIN takes over from your CSV files, and try a month first before you rely on it.</li>
      </ol>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          void run(() => send('POST', '/imports/simplefin/connect', { setupToken: token })).then(() => setToken(''))
        }}
      >
        <label>
          Setup token
          <textarea value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" spellCheck={false} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button type="submit" className="btn primary" disabled={busy || token.trim() === ''}>
            Connect
          </button>
        </div>
      </form>
    </Card>
  )
}

function Connected({ status }: { status: Status }) {
  const sync = useAct()
  const off = useAct()
  const [report, setReport] = useState<SyncReport | null>(null)

  return (
    <>
      <Card
        title="SimpleFIN"
        aside={
          <>
            <button type="button" className="btn primary" disabled={sync.busy} onClick={async () => setReport((await sync.run(() => send<SyncReport>('POST', '/imports/simplefin/sync', {}))) ?? null)}>
              {sync.busy ? 'Syncing…' : 'Sync now'}
            </button>{' '}
            <button
              type="button"
              className="btn"
              disabled={off.busy}
              onClick={() => {
                if (window.confirm(DISCONNECT_WARNING)) void off.run(() => send('DELETE', '/imports/simplefin'))
              }}
            >
              Disconnect
            </button>
          </>
        }
      >
        <p>
          <Badge tone="good">Connected</Badge> Last synced {status.lastSyncAt ? when(status.lastSyncAt) : 'never'}. It syncs about every {status.everyHours} hours. {status.requestsToday} of {status.requestLimit} requests used today.
        </p>
        {status.lastError && <p className="problem">SimpleFIN says: {status.lastError}</p>}
        <p className="muted">
          Disconnect only makes Tideline forget the connection. It does not cancel it at SimpleFIN: to stop access completely, also revoke it at SimpleFIN Bridge.
        </p>
        <Problem message={sync.error ?? off.error} />
        {report && (
          <div role="status">
            {report.errors.length > 0 && <p className="problem">{report.errors.join(' ')}</p>}
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Account</th>
                    <th className="num">New</th>
                    <th className="num">Already there</th>
                    <th className="num">Waiting to post</th>
                    <th className="num">Before the takeover</th>
                    <th>Balance</th>
                  </tr>
                </thead>
                <tbody>
                  {report.accounts.map((a) => (
                    <tr key={a.accountId}>
                      <td>
                        {a.accountName}
                        {a.error && <div className="problem">{a.error}</div>}
                      </td>
                      <td className="num">{a.inserted}</td>
                      <td className="num">{a.existing}</td>
                      <td className="num">{a.pendingSkipped}</td>
                      <td className="num">{a.beforeCutover}</td>
                      <td>{a.balanceMatches === null ? '' : a.balanceMatches ? <Badge tone="good">matches the bank</Badge> : <Badge tone="bad">does not match</Badge>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </Card>
      <Matching status={status} />
      <TryFirst status={status} />
    </>
  )
}

function Matching({ status }: { status: Status }) {
  const remote = useQuery({ queryKey: ['settings', 'simplefin-accounts'], queryFn: () => get<Remote[]>('/imports/simplefin/accounts'), retry: false })
  const ours = useQuery({ queryKey: ['accounts-list'], queryFn: () => get<Ours[]>('/accounts') })
  const { run, error, busy } = useAct()
  const [choice, setChoice] = useState<Record<string, { account: string; from: string }>>({})

  return (
    <Card title="Match their accounts to yours">
      <p className="muted">
        Pick the day SimpleFIN takes over from the CSV files for each account. CSV files are refused after that day and SimpleFIN lines before it are ignored, so the two never cover the
        same days.
      </p>
      {remote.isLoading && <Loading what="their accounts" />}
      {remote.error && <ErrorBox error={remote.error} />}
      <Problem message={error} />
      {remote.data && ours.data && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>At SimpleFIN</th>
                <th className="num">Balance</th>
                <th>Matched to</th>
                <th>Takes over on</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {remote.data.map((r) => {
                const current = status.matched.find((m) => m.simplefinAccountId === r.id)
                const c = choice[r.id] ?? { account: String(current?.accountId ?? r.suggestedAccountId ?? ''), from: current?.takesOverOn ?? '' }
                const set = (patch: Partial<typeof c>) => setChoice({ ...choice, [r.id]: { ...c, ...patch } })
                return (
                  <tr key={r.id}>
                    <td>
                      {r.name} <span className="muted">{r.organization}</span>
                    </td>
                    <td className="num">{dollars(r.balanceCents)}</td>
                    <td>
                      <select aria-label={`Match ${r.name}`} value={c.account} onChange={(e) => set({ account: e.target.value })}>
                        <option value="">Not matched</option>
                        {ours.data.filter((a) => ['checking', 'savings', 'credit_card'].includes(a.kind)).map((a) => (
                          <option key={a.id} value={a.id}>
                            {a.name}
                          </option>
                        ))}
                      </select>
                    </td>
                    <td>
                      <input type="date" aria-label={`Takes over for ${r.name}`} value={c.from} onChange={(e) => set({ from: e.target.value })} />
                    </td>
                    <td>
                      <button
                        type="button"
                        className="btn"
                        disabled={busy || c.account === '' || c.from === ''}
                        onClick={() => void run(() => send('PATCH', `/accounts/${c.account}`, { simplefinAccountId: r.id, simplefinFromOn: c.from }))}
                      >
                        Save
                      </button>
                      {current && (
                        <button
                          type="button"
                          className="btn"
                          disabled={busy}
                          onClick={() => void run(() => send('PATCH', `/accounts/${current.accountId}`, { clearSimplefin: true }))}
                        >
                          Unmatch
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

function TryFirst({ status }: { status: Status }) {
  const { run, error, busy } = useAct()
  const [account, setAccount] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [result, setResult] = useState<DryRun | null>(null)

  if (status.matched.length === 0) return null
  return (
    <Card title="Try a month first">
      <p className="muted">
        Before letting SimpleFIN take over, set a month of it beside the CSV for the same days. Nothing is saved. You will see which lines SimpleFIN describes differently enough that the
        rules would put them somewhere else.
      </p>
      <div className="form">
        <label>
          Account
          <select value={account} onChange={(e) => setAccount(e.target.value)}>
            <option value="">Pick one</option>
            {status.matched.map((m) => (
              <option key={m.accountId} value={m.accountId}>
                {m.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>
          To (up to 90 days)
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button
            type="button"
            className="btn"
            disabled={busy || account === '' || from === '' || to === ''}
            onClick={async () => setResult((await run(() => send<DryRun>('POST', '/imports/simplefin/dry-run', { accountId: Number(account), from, to }))) ?? null)}
          >
            Compare
          </button>
        </div>
      </div>

      {result && (
        <div role="status">
          <p>
            {result.accountName}, {result.from} to {result.to}: SimpleFIN sent {result.simpleFinRows} lines. {result.matched} match a line in the ledger, and {result.sameMeaning} of those would be
            sorted the same way. <strong>{result.different.length}</strong> would land differently, <strong>{result.onlyInSimpleFin.length}</strong> are only in SimpleFIN and{' '}
            <strong>{result.onlyInLedger.length}</strong> are only in the ledger.
          </p>
          <DryTable title="Would land differently" lines={result.different} />
          <DryTable title="Only in SimpleFIN" lines={result.onlyInSimpleFin} />
          <DryTable title="Only in the ledger" lines={result.onlyInLedger} />
        </div>
      )}
    </Card>
  )
}

function DryTable({ title, lines }: { title: string; lines: DryLine[] }) {
  if (lines.length === 0) return null
  return (
    <div className="table-wrap">
      <table>
        <caption>{title}</caption>
        <thead>
          <tr>
            <th>Date</th>
            <th>SimpleFIN says</th>
            <th>The CSV said</th>
            <th className="num">Amount</th>
            <th>What changes</th>
          </tr>
        </thead>
        <tbody>
          {lines.map((l, i) => (
            <tr key={i}>
              <td>{l.postedOn}</td>
              <td>{l.description}</td>
              <td>{l.ledgerDescription ?? ''}</td>
              <td className="num">{dollars(l.amountCents)}</td>
              <td>{l.difference}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
