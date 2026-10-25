import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../api'
import { Badge, Card, ErrorBox, Loading, Money } from '../components'
import { dollars, monthLabel, parseDollars, typeLabel } from '../format'
import { useMe } from '../auth'
import type { BudgetData, BudgetProposals } from '../phase4'
import type { Period } from '../period'
import { Problem, centsText, useAct } from './settings/common'

type Kind = 'monthly' | 'annual'
type Draft = Record<number, { kind: Kind; amount: string }>

function draftFrom(data: BudgetData): Draft {
  const d: Draft = {}
  for (const c of data.categories) {
    if (c.monthlyCents !== null) d[c.categoryId] = { kind: 'monthly', amount: centsText(c.monthlyCents) }
    else if (c.annualCents !== null) d[c.categoryId] = { kind: 'annual', amount: centsText(c.annualCents) }
  }
  return d
}

export function Budget({ period }: { period: Period }) {
  const me = useMe()
  const canEdit = me.user.role !== 'viewer'
  const year = period.year
  const data = useQuery({ queryKey: ['budget', year], queryFn: () => get<BudgetData>(`/budgets/${year}`) })
  const [draft, setDraft] = useState<Draft | null>(null)
  const [income, setIncome] = useState('')
  const [proposal, setProposal] = useState<BudgetProposals | null>(null)
  const { run, error, busy } = useAct()

  if (data.isLoading) return <Loading what="the budget" />
  if (data.error || !data.data) return <ErrorBox error={data.error} />
  const b = data.data
  const editing = draft !== null

  const start = () => {
    setDraft(draftFrom(b))
    setIncome(centsText(b.income.expectedAnnualCents))
  }

  const fill = async () => {
    const p = await run(() => send<BudgetProposals>('POST', `/budgets/${year}/fill-from-averages`))
    if (!p) return
    setProposal(p)
    // proposals fill the draft only where there is no guideline yet; nothing is saved until Save
    const next = { ...(draft ?? draftFrom(b)) }
    for (const item of p.items) if (!next[item.categoryId]) next[item.categoryId] = { kind: 'monthly', amount: centsText(item.monthlyCents) }
    setDraft(next)
    if (draft === null) setIncome(centsText(b.income.expectedAnnualCents))
  }

  const invalid = editing && (Object.values(draft).some((d) => d.amount.trim() !== '' && parseDollars(d.amount) === null) || (income.trim() !== '' && parseDollars(income) === null))

  const save = async () => {
    if (!draft) return
    const lines = Object.entries(draft)
      .filter(([, d]) => d.amount.trim() !== '')
      .map(([id, d]) => ({ categoryId: Number(id), monthlyCents: d.kind === 'monthly' ? parseDollars(d.amount) : null, annualCents: d.kind === 'annual' ? parseDollars(d.amount) : null }))
    const done = await run(() =>
      send('PUT', `/budgets/${year}`, { lines, expectedIncomeCents: income.trim() === '' ? null : parseDollars(income), clearExpectedIncome: income.trim() === '' }),
    )
    if (done !== undefined) {
      setDraft(null)
      setProposal(null)
    }
  }

  const popped = b.categories.filter((c) => c.popped)

  return (
    <>
      <h1>Budget, {b.year}</h1>

      <Card
        title="Income this year"
        aside={
          canEdit &&
          !editing && (
            <button type="button" className="btn" onClick={start}>
              Edit the budget
            </button>
          )
        }
      >
        {editing ? (
          <label className="form">
            Expected income for the year
            <input value={income} inputMode="decimal" onChange={(e) => setIncome(e.target.value)} placeholder="0.00" />
          </label>
        ) : (
          <div className="grid cols-3">
            <div>
              <div className="muted">Expected for the year</div>
              <span className="big">{b.income.expectedAnnualCents === null ? <span className="muted">not set</span> : dollars(b.income.expectedAnnualCents)}</span>
            </div>
            <div>
              <div className="muted">Expected so far ({b.elapsedMonths} of 12 months)</div>
              <span className="big">{b.income.expectedYtdCents === null ? '' : dollars(b.income.expectedYtdCents)}</span>
            </div>
            <div>
              <div className="muted">Received so far</div>
              <span className="big">
                <Money cents={b.income.actualYtd.cents} drill={b.income.actualYtd.drill} />
              </span>{' '}
              {b.income.short && <Badge tone="info">under 90% of the pace</Badge>}
            </div>
          </div>
        )}
      </Card>

      {popped.length > 0 && !editing && (
        <Card title="Over the guideline">
          <ul>
            {popped.map((c) => (
              <li key={c.categoryId}>
                {c.name}: {c.monthlyCents !== null ? `${dollars(c.thisMonth.cents)} this month against ${dollars(c.monthlyCents)}` : `${dollars(c.ytd.cents)} so far against ${dollars(c.annualCents ?? 0)} for the year`}
              </li>
            ))}
          </ul>
        </Card>
      )}

      <Card
        title="Guidelines by category"
        aside={
          editing ? (
            <>
              <button type="button" className="btn" disabled={busy} onClick={() => void fill()}>
                Fill from last year&rsquo;s averages
              </button>{' '}
              <button type="button" className="btn" onClick={() => { setDraft(null); setProposal(null) }}>
                Cancel
              </button>{' '}
              <button type="button" className="btn primary" disabled={busy || invalid} onClick={() => void save()}>
                Save the budget
              </button>
            </>
          ) : (
            canEdit && (
              <button type="button" className="btn" onClick={() => void fill().then(() => undefined)} disabled={busy}>
                Fill from last year&rsquo;s averages
              </button>
            )
          )
        }
      >
        <p className="muted">
          A monthly guideline pops when this month&rsquo;s spending is over it by {b.popPercent}% and by more than {dollars(b.popMinCents)}. An annual one pops when the year so far is
          over it. Month shown: {monthLabel(b.month)}.
        </p>
        <Problem message={error} />
        {proposal && (
          <p role="status">
            Proposed from {proposal.from} to {proposal.to}: each category&rsquo;s average month, rounded up to ten dollars. Look them over, change what you like, then save. Nothing is kept
            until you do.
          </p>
        )}
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Category</th>
                <th>Guideline</th>
                <th className="num">This month</th>
                <th className="num">Year so far</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {b.categories.map((c) => {
                const d = draft?.[c.categoryId]
                return (
                  <tr key={c.categoryId}>
                    <td>
                      {c.name} <span className="muted">({typeLabel(c.type)})</span>
                    </td>
                    <td>
                      {editing ? (
                        <span className="guide">
                          <input
                            aria-label={`${c.name} guideline`}
                            value={d?.amount ?? ''}
                            inputMode="decimal"
                            placeholder="none"
                            onChange={(e) => setDraft({ ...draft, [c.categoryId]: { kind: d?.kind ?? 'monthly', amount: e.target.value } })}
                          />
                          <select
                            aria-label={`${c.name} per`}
                            value={d?.kind ?? 'monthly'}
                            onChange={(e) => setDraft({ ...draft, [c.categoryId]: { kind: e.target.value as Kind, amount: d?.amount ?? '' } })}
                          >
                            <option value="monthly">a month</option>
                            <option value="annual">a year</option>
                          </select>
                        </span>
                      ) : c.monthlyCents !== null ? (
                        `${dollars(c.monthlyCents)} a month`
                      ) : c.annualCents !== null ? (
                        `${dollars(c.annualCents)} a year`
                      ) : (
                        <span className="muted">none</span>
                      )}
                    </td>
                    <td className="num">{c.thisMonth.cents === 0 ? <span className="muted">–</span> : <Money cents={c.thisMonth.cents} drill={c.thisMonth.drill} />}</td>
                    <td className="num">{c.ytd.cents === 0 ? <span className="muted">–</span> : <Money cents={c.ytd.cents} drill={c.ytd.drill} />}</td>
                    <td>{c.popped && <Badge tone="warn">over</Badge>}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </Card>
    </>
  )
}
