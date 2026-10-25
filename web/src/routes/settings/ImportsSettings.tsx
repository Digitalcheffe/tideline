import { useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get, upload } from '../../api'
import { Badge, Card, Empty, ErrorBox, Loading } from '../../components'
import { dollars } from '../../format'
import { Problem, useAct, when } from './common'

interface Batch {
  id: number
  source: string
  fileName: string | null
  accountName: string | null
  startedAt: string
  finishedAt: string | null
  status: 'running' | 'done' | 'failed'
  rowsRead: number
  rowsInserted: number
  rowsExisting: number
  rowsReview: number
  error: string | null
}

interface Check {
  accountId: number
  accountName: string
  asOf: string
  bankCents: number
  ledgerCents: number
  matches: boolean
  differenceCents: number
}

interface ImportsPage {
  inbox: { configured: boolean; path: string | null; waiting: number; settling: number; failed: number }
  batches: Batch[]
  balanceChecks: Check[]
  maxUploadBytes: number
}

interface UploadResult {
  fileName: string
  status: 'done' | 'failed'
  rowsRead: number
  rowsInserted: number
  rowsExisting: number
  rowsReview: number
  movesPaired: number
  movesPending: number
  error: string | null
  reconciliation: { asOf: string; bankCents: number; ledgerCents: number; matches: boolean } | null
}

export function ImportsSettings() {
  const page = useQuery({ queryKey: ['settings', 'imports'], queryFn: () => get<ImportsPage>('/imports') })
  const picker = useRef<HTMLInputElement>(null)
  const { run, error, busy } = useAct()
  const [result, setResult] = useState<UploadResult | null>(null)

  if (page.isLoading) return <Loading what="imports" />
  if (page.error || !page.data) return <ErrorBox error={page.error} />
  const { inbox, batches, balanceChecks } = page.data
  const bad = balanceChecks.filter((c) => !c.matches)

  const send = async (file: File | undefined) => {
    if (!file) return
    setResult(null)
    const done = await run(() => upload<UploadResult>('/imports/upload', file))
    if (done) setResult(done)
    if (picker.current) picker.current.value = ''
  }

  return (
    <>
      <Card title="Bring in a file">
        <p className="muted">
          Drop the bank's CSV into the inbox folder, or send it from here. Both go through the same checks. Lines already seen are skipped, so sending the
          same file twice is safe.
        </p>
        <div className="toolbar">
          <input ref={picker} type="file" accept=".csv,text/csv" aria-label="CSV file" onChange={(e) => void send(e.target.files?.[0])} disabled={busy} />
          {busy && <span role="status">Importing…</span>}
        </div>
        <Problem message={error} />
        {result && (
          <div role="status">
            {result.status === 'failed' ? (
              <p className="problem">
                {result.fileName} was not imported. {result.error}
              </p>
            ) : (
              <p>
                <Badge tone="good">Imported</Badge> {result.fileName}: {result.rowsRead} rows read, {result.rowsInserted} new, {result.rowsExisting} already there
                {result.rowsReview > 0 && (
                  <>
                    , <Link to="/lines?review=true">{result.rowsReview} to review</Link>
                  </>
                )}
                {result.movesPaired > 0 && `, ${result.movesPaired} transfers paired`}
                {result.reconciliation &&
                  (result.reconciliation.matches ? (
                    <>
                      {' '}
                      <Badge tone="good">balance matches the bank</Badge>
                    </>
                  ) : (
                    <>
                      {' '}
                      <Badge tone="bad">balance is off</Badge> bank says {dollars(result.reconciliation.bankCents)}, ledger says {dollars(result.reconciliation.ledgerCents)}
                    </>
                  ))}
                .
              </p>
            )}
          </div>
        )}
      </Card>

      <Card title="Inbox folder">
        {!inbox.configured ? (
          <p className="muted">No inbox folder is set (Tideline:InboxPath), so files can only be sent from this page.</p>
        ) : (
          <p>
            <code>{inbox.path}</code>: {inbox.waiting} waiting, {inbox.settling} still being written, {inbox.failed} in the Failed folder.
          </p>
        )}
      </Card>

      <Card title="Does the ledger agree with the bank?">
        {balanceChecks.length === 0 ? (
          <p className="muted">No bank balances yet. They arrive with the first import.</p>
        ) : (
          <>
            {bad.length > 0 && (
              <p className="problem" role="alert">
                {bad.length === 1 ? 'One account does' : `${bad.length} accounts do`} not agree. A missing file or a missing line is the usual cause.
              </p>
            )}
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Account</th>
                    <th>As of</th>
                    <th className="num">Bank says</th>
                    <th className="num">Ledger says</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {balanceChecks.map((c) => (
                    <tr key={c.accountId}>
                      <td>{c.accountName}</td>
                      <td>{c.asOf}</td>
                      <td className="num">{dollars(c.bankCents)}</td>
                      <td className="num">{dollars(c.ledgerCents)}</td>
                      <td>{c.matches ? <Badge tone="good">matches</Badge> : <Badge tone="bad">off by {dollars(Math.abs(c.differenceCents))}</Badge>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Card>

      <Card title="Recent imports">
        {batches.length === 0 ? (
          <Empty>Nothing has been imported yet.</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>File</th>
                  <th>Account</th>
                  <th className="num">Read</th>
                  <th className="num">New</th>
                  <th className="num">Already there</th>
                  <th className="num">To review</th>
                  <th>Result</th>
                </tr>
              </thead>
              <tbody>
                {batches.map((b) => (
                  <tr key={b.id}>
                    <td>{when(b.startedAt)}</td>
                    <td>{b.fileName ?? b.source}</td>
                    <td>{b.accountName ?? ''}</td>
                    <td className="num">{b.rowsRead}</td>
                    <td className="num">{b.rowsInserted}</td>
                    <td className="num">{b.rowsExisting}</td>
                    <td className="num">{b.rowsReview}</td>
                    <td>
                      {b.status === 'done' ? <Badge tone="good">done</Badge> : b.status === 'running' ? <Badge tone="info">running</Badge> : <Badge tone="bad">failed</Badge>}
                      {b.error && <div className="muted">{b.error}</div>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  )
}
