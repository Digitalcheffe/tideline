import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { get, send } from '../api'
import { Badge, Card, ErrorBox, Loading } from '../components'
import { dateLabel, dollars, monthLabel, parseDollars, shortMonth } from '../format'
import { useMe } from '../auth'
import { bpsText, type Countdown, type LoanView, type NetWorthData, type NwLine } from '../phase4'
import { Panel, Problem, useAct } from './settings/common'

interface Pickable {
  id: number
  name: string
  kind: string
}

const KIND_LABEL: Record<string, string> = {
  checking: 'Checking',
  savings: 'Savings',
  credit_card: 'Credit card',
  loan: 'Loan',
  asset: 'Asset',
  retirement: 'Retirement',
}

const today = () => new Date().toISOString().slice(0, 10)

/** Type in what a loan, a house or a retirement account is worth on a day. Owners only: the server says no to everyone else. */
export function SnapshotForm({ accounts, accountId, onClose }: { accounts: Pickable[]; accountId?: number; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [id, setId] = useState(accountId ? String(accountId) : '')
  const [asOn, setAsOn] = useState(today())
  const [amount, setAmount] = useState('')
  const [source, setSource] = useState('statement')
  const [note, setNote] = useState('')

  const cents = parseDollars(amount)
  const picked = accounts.find((a) => String(a.id) === id)
  const valid = picked !== undefined && cents !== null && cents >= 0 && asOn !== ''

  return (
    <Panel title="Enter a figure" onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (valid) void run(() => send('POST', `/accounts/${id}/snapshots`, { asOn, balanceCents: cents, source, note }))
        }}
      >
        <label>
          Which one
          <select value={id} onChange={(e) => setId(e.target.value)} disabled={accountId !== undefined}>
            <option value="">Pick an account</option>
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name} ({KIND_LABEL[a.kind] ?? a.kind})
              </option>
            ))}
          </select>
        </label>
        <label>
          {picked?.kind === 'loan' ? 'What is owed' : 'What it is worth'}
          <input value={amount} inputMode="decimal" onChange={(e) => setAmount(e.target.value)} placeholder="0.00" />
        </label>
        <label>
          As of
          <input type="date" value={asOn} max={today()} onChange={(e) => setAsOn(e.target.value)} />
        </label>
        <label>
          Where it came from
          <select value={source} onChange={(e) => setSource(e.target.value)}>
            <option value="statement">A statement</option>
            <option value="estimate">My own estimate</option>
          </select>
        </label>
        <label>
          Note (optional)
          <input value={note} maxLength={400} onChange={(e) => setNote(e.target.value)} />
        </label>
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

function AsOf({ line }: { line: NwLine }) {
  return (
    <span className="muted">
      as of {line.asOf ? dateLabel(line.asOf) : 'unknown'}
      {line.source !== 'ledger' && line.source !== 'bank' ? ` (${line.source})` : ''} {line.stale && <Badge tone="warn">stale</Badge>}
    </span>
  )
}

function Lines({ lines, onUpdate, canEdit }: { lines: NwLine[]; onUpdate: (id: number) => void; canEdit: boolean }) {
  if (lines.length === 0) return <p className="muted">Nothing here yet.</p>
  return (
    <div className="table-wrap">
      <table>
        <tbody>
          {lines.map((l) => (
            <tr key={`${l.kind}-${l.accountId}`}>
              <td>
                {l.name}
                <div>
                  <AsOf line={l} />
                </div>
              </td>
              <td className="num">{dollars(l.cents)}</td>
              <td>
                {canEdit && ['loan', 'asset', 'retirement'].includes(l.kind) && (
                  <button type="button" className="btn" onClick={() => onUpdate(l.accountId)}>
                    Update
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function LoanCard({ loan }: { loan: LoanView }) {
  const [extra, setExtra] = useState(0)
  const effect = useQuery({
    queryKey: ['countdown', loan.accountId, extra, loan.balanceCents],
    queryFn: () => get<Countdown>(`/loans/${loan.accountId}/countdown`, { extraCents: String(extra * 100) }),
    enabled: loan.payoff !== null,
  })

  return (
    <div className="loan">
      <h3>
        {loan.name} <span className="muted">({loan.loanType})</span>
      </h3>
      {loan.balanceCents === null ? (
        <p className="muted">Enter what is owed to see the countdown.</p>
      ) : (
        <p>
          <strong>{dollars(loan.balanceCents)}</strong> owed{' '}
          <span className="muted">
            as of {loan.asOf ? dateLabel(loan.asOf) : ''}
            {loan.source === 'computed' ? ' (moved on by each payment)' : ''} {loan.stale && <Badge tone="warn">stale</Badge>}
          </span>
        </p>
      )}
      {loan.missingTerms && <p className="muted">Enter the rate and the monthly payment in Settings, Money accounts, to see the countdown.</p>}
      {loan.payoff && loan.rateBps !== null && loan.paymentCents !== null && (
        <>
          {loan.payoff.paysOff ? (
            <p>
              At {bpsText(loan.rateBps)} and {dollars(loan.paymentCents)} a month: paid off in <strong>{loan.payoff.payoffMonth ? monthLabel(loan.payoff.payoffMonth) : ''}</strong> (
              {loan.payoff.monthsLeft?.toFixed(1)} months), with <strong>{dollars(loan.payoff.interestLeftCents)}</strong> of interest still to pay.
            </p>
          ) : (
            <p className="problem">This payment does not cover the interest, so the balance never goes down.</p>
          )}
          <label className="slider">
            Pay an extra <strong>${extra}</strong> a month
            <input type="range" min={0} max={2000} step={25} value={extra} onChange={(e) => setExtra(Number(e.target.value))} aria-label="Extra per month" />
          </label>
          {extra > 0 && effect.data && (
            <p role="status">
              {effect.data.monthsSaved === null ? (
                <>It would be paid off in {effect.data.with.payoffMonth ? monthLabel(effect.data.with.payoffMonth) : ''}.</>
              ) : (
                <>
                  Paid off {effect.data.monthsSaved.toFixed(1)} months sooner, in {effect.data.with.payoffMonth ? monthLabel(effect.data.with.payoffMonth) : ''}, saving{' '}
                  <strong>{dollars(effect.data.interestSavedCents)}</strong> of interest.
                </>
              )}
            </p>
          )}
        </>
      )}
    </div>
  )
}

export function NetWorth() {
  const me = useMe()
  const canEdit = me.user.role === 'owner'
  const data = useQuery({ queryKey: ['net-worth'], queryFn: () => get<NetWorthData>('/net-worth') })
  const accounts = useQuery({ queryKey: ['accounts-list'], queryFn: () => get<Pickable[]>('/accounts'), enabled: canEdit })
  const [form, setForm] = useState<{ accountId?: number } | null>(null)

  if (data.isLoading) return <Loading what="net worth" />
  if (data.error || !data.data) return <ErrorBox error={data.error} />
  const nw = data.data
  const pickable = (accounts.data ?? []).filter((a) => ['loan', 'asset', 'retirement'].includes(a.kind))

  return (
    <>
      {form && canEdit && <SnapshotForm key={form.accountId ?? 'new'} accounts={pickable} accountId={form.accountId} onClose={() => setForm(null)} />}

      <div className="grid cols-3">
        <Card title="Assets">
          <span className="big">{dollars(nw.assetsCents)}</span>
        </Card>
        <Card title="Liabilities">
          <span className="big">{dollars(nw.liabilitiesCents)}</span>
        </Card>
        <Card title="Net worth">
          <span className="big">{dollars(nw.netWorthCents)}</span>
          <div className="muted">as of {dateLabel(nw.asOf)}</div>
        </Card>
      </div>

      {nw.needsInput.length > 0 && (
        <Card
          title="To fill in"
          aside={
            canEdit && (
              <button type="button" className="btn primary" onClick={() => setForm({})}>
                Enter a figure
              </button>
            )
          }
        >
          <ul>
            {nw.needsInput.map((n) => (
              <li key={n}>{n}</li>
            ))}
          </ul>
          {!canEdit && <p className="muted">An owner can enter these.</p>}
        </Card>
      )}

      <div className="grid cols-2">
        <Card
          title="Assets"
          aside={
            canEdit &&
            nw.needsInput.length === 0 && (
              <button type="button" className="btn" onClick={() => setForm({})}>
                Enter a figure
              </button>
            )
          }
        >
          <Lines lines={nw.assets} canEdit={canEdit} onUpdate={(id) => setForm({ accountId: id })} />
        </Card>
        <Card title="Liabilities">
          <Lines lines={nw.liabilities} canEdit={canEdit} onUpdate={(id) => setForm({ accountId: id })} />
          <p className="muted">A card counts as what you owe on it. Estimates older than 90 days are marked stale.</p>
        </Card>
      </div>

      {nw.equity.length > 0 && (
        <Card title="Equity">
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>What</th>
                  <th className="num">Worth</th>
                  <th>Owed on</th>
                  <th className="num">Owed</th>
                  <th className="num">Equity</th>
                </tr>
              </thead>
              <tbody>
                {nw.equity.map((e) => (
                  <tr key={`${e.assetId}-${e.loanId}`}>
                    <td>
                      {e.assetName} {e.assetStale && <Badge tone="warn">stale</Badge>}
                      <div className="muted">as of {e.assetAsOf ? dateLabel(e.assetAsOf) : ''}</div>
                    </td>
                    <td className="num">{dollars(e.assetCents)}</td>
                    <td>{e.loanName}</td>
                    <td className="num">{dollars(e.loanCents)}</td>
                    <td className="num">
                      <strong>{dollars(e.equityCents)}</strong>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      {nw.loans.length > 0 && (
        <Card title="Debt countdown">
          {nw.loans.map((l) => (
            <LoanCard key={l.accountId} loan={l} />
          ))}
        </Card>
      )}

      <Card title="Net worth by month end">
        {nw.trend.length < 2 ? (
          <p className="muted">The trend appears once there are two months of history.</p>
        ) : (
          <div style={{ width: '100%', height: 240 }}>
            <ResponsiveContainer>
              <LineChart data={nw.trend.map((t) => ({ ...t, label: shortMonth(t.month) }))} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                <XAxis dataKey="label" stroke="var(--muted)" />
                <YAxis stroke="var(--muted)" tickFormatter={(v: number) => `$${Math.round(v / 100).toLocaleString('en-US')}`} width={72} />
                <Tooltip formatter={(v) => dollars(Number(v))} contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)' }} />
                <Line dataKey="netWorthCents" name="Net worth" stroke="var(--accent)" strokeWidth={2} dot={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        )}
      </Card>
    </>
  )
}
