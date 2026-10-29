import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { Bar, CartesianGrid, ComposedChart, Legend, Line, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { get, type Overview as OverviewData } from '../api'
import { Badge, Card, ErrorBox, Loading, Money, ShareBar, TYPE_COLORS, drillPath } from '../components'
import { dollars, monthLabel, shortMonth } from '../format'
import { useNeedsCategory } from '../needs'
import type { Period } from '../period'
import { NetWorth } from './NetWorth'
import { NeedsYou } from './NeedsYou'
import { STATUS_LABEL, STATUS_TONE } from '../phase4'

const ITEMS: { key: 'in' | 'bills' | 'required' | 'extra' | 'oneTime' | 'savings'; label: string }[] = [
  { key: 'in', label: 'In' },
  { key: 'bills', label: 'Bills' },
  { key: 'required', label: 'Required' },
  { key: 'extra', label: 'Extra' },
  { key: 'oneTime', label: 'One-time' },
  { key: 'savings', label: 'Savings' },
]

/** Lines waiting for a category, live. Shown only while there are some; the number opens those lines in the Ledger. */
function NeedsTile() {
  const { data } = useNeedsCategory()
  if (!data || data.count === 0) return null
  const lines = `${data.count} ${data.count === 1 ? 'line needs' : 'lines need'} a category`
  return (
    <section className="card needs-tile" aria-label="Needs a category" style={{ marginBottom: 12 }}>
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <div>
          <strong>
            <Link to={drillPath(data.drill)!} title="Show the lines behind this number">
              {lines}
            </Link>
          </strong>
          {data.checks > 0 && <span className="muted"> · {data.checks} {data.checks === 1 ? 'is a check' : 'are checks'}</span>}
          <div className="muted">The rules could not place them. Answer them and they leave this count at once.</div>
        </div>
        <Link className="btn primary" to="/needs">
          Answer them
        </Link>
      </div>
    </section>
  )
}

export function Overview({ period }: { period: Period }) {
  const [params, setParams] = useSearchParams()
  const tab = params.get('tab') === 'networth' ? 'networth' : 'overview'
  const choose = (next: string) => {
    const p = new URLSearchParams(params)
    if (next === 'overview') p.delete('tab')
    else p.set('tab', next)
    setParams(p)
  }
  const { data, error, isLoading } = useQuery({
    queryKey: ['overview', period.from, period.to],
    queryFn: () => get<OverviewData>('/overview', { from: period.from, to: period.to }),
  })

  if (isLoading) return <Loading what="the overview" />
  if (error || !data) return <ErrorBox error={error} />

  const s = data.spread
  const out = ITEMS.filter((i) => i.key !== 'in')
  const chart = data.months.map((m) => ({ ...m, label: shortMonth(m.month) }))
  const hasData = s.in.cents !== 0 || s.out.cents !== 0

  return (
    <>
      <h1>Overview</h1>
      <div className="seg" role="tablist" style={{ marginBottom: 12 }}>
        <button type="button" className={tab === 'overview' ? 'on' : ''} role="tab" aria-selected={tab === 'overview'} onClick={() => choose('overview')}>
          Overview
        </button>
        <button type="button" className={tab === 'networth' ? 'on' : ''} role="tab" aria-selected={tab === 'networth'} onClick={() => choose('networth')}>
          Net worth
        </button>
      </div>

      {tab === 'networth' ? (
        <NetWorth />
      ) : (
        <>

      <NeedsTile />

      <Card title="Where the money went" aside={`${data.from} to ${data.to}`}>
        {!hasData ? (
          <p className="muted">Nothing in this period yet. Import a bank export, or pick another period.</p>
        ) : (
          <>
            <ShareBar
              parts={[
                ...out.map((i) => ({ key: i.key, label: i.label, cents: s[i.key].cents })),
                { key: 'leftOver', label: 'Left over', cents: s.leftOver.cents },
              ]}
            />
            <div className="spread">
              <div className="spread-item" style={{ ['--c' as string]: TYPE_COLORS.in }}>
                <div className="label">In</div>
                <div className="value">
                  <Money cents={s.in.cents} drill={s.in.drill} />
                </div>
              </div>
              {out.map((i) => (
                <div className="spread-item" key={i.key} style={{ ['--c' as string]: TYPE_COLORS[i.key] }}>
                  <div className="label">{i.label}</div>
                  <div className="value">
                    <Money cents={s[i.key].cents} drill={s[i.key].drill} />
                  </div>
                </div>
              ))}
              <div className="spread-item" style={{ ['--c' as string]: TYPE_COLORS.leftOver }}>
                <div className="label">Left over</div>
                <div className="value">
                  <Money cents={s.leftOver.cents} />
                </div>
              </div>
            </div>
            <p className="equation">
              In {dollars(s.in.cents)} = {out.map((i) => `${i.label} ${dollars(s[i.key].cents)}`).join(' + ')} + Left over {dollars(s.leftOver.cents)}
            </p>
          </>
        )}
      </Card>

      <div className="grid cols-2" style={{ marginTop: 12 }}>
        <Card title="Monthly spread" aside="Stacked by type; In is the line">
          {chart.length === 0 ? (
            <p className="muted">No months with data yet.</p>
          ) : (
            <div style={{ width: '100%', height: 260 }}>
              <ResponsiveContainer>
                <ComposedChart data={chart} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                  <XAxis dataKey="label" stroke="var(--muted)" />
                  <YAxis stroke="var(--muted)" tickFormatter={(v: number) => `$${Math.round(v / 100).toLocaleString('en-US')}`} width={64} />
                  <Tooltip formatter={(v) => dollars(Number(v))} contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)' }} />
                  <Legend />
                  <Bar dataKey="bills" name="Bills" stackId="s" fill={TYPE_COLORS.bills} />
                  <Bar dataKey="required" name="Required" stackId="s" fill={TYPE_COLORS.required} />
                  <Bar dataKey="extra" name="Extra" stackId="s" fill={TYPE_COLORS.extra} />
                  <Bar dataKey="oneTime" name="One-time" stackId="s" fill={TYPE_COLORS.oneTime} />
                  <Bar dataKey="savings" name="Savings" stackId="s" fill={TYPE_COLORS.savings} />
                  <Line dataKey="in" name="In" stroke={TYPE_COLORS.in} strokeWidth={2} dot />
                </ComposedChart>
              </ResponsiveContainer>
            </div>
          )}
        </Card>

        <div>
          <Card title="Plans" aside={<Link to="/planning">All plans</Link>}>
            {data.plans.length === 0 ? (
              <p className="muted">No trackable plans yet. Make one on the Planning screen.</p>
            ) : (
              data.plans.map((p) => (
                <div key={p.id} className="plan-row">
                  <div className="plan-head">
                    <Link to="/planning">{p.name}</Link>{' '}
                    <Badge tone={STATUS_TONE[p.status]}>{STATUS_LABEL[p.status]}</Badge>
                  </div>
                  <progress max={p.targetCents} value={Math.min(Math.max(p.actualCents, 0), p.targetCents)} aria-label={`${p.name}: ${dollars(p.actualCents)} of ${dollars(p.targetCents)}`} />
                  <div className="muted">
                    {dollars(p.actualCents)} of {dollars(p.targetCents)}
                    {p.trouble ? ', ' + (p.trouble.status === 'behind' ? 'behind' : 'wavered') + ' in ' + monthLabel(p.trouble.month) : ''}
                  </div>
                </div>
              ))
            )}
          </Card>
          <NeedsYou data={data} />
        </div>
      </div>
        </>
      )}
    </>
  )
}
