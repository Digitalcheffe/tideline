import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { get, type Cards as CardsData } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money } from '../components'
import { dateLabel, monthLabel, shortMonth } from '../format'
import type { Period } from '../period'

export function Cards({ period }: { period: Period }) {
  const [selected, setSelected] = useState<number | null>(null)
  const { data, error, isLoading } = useQuery({
    queryKey: ['cards', period.month],
    queryFn: () => get<CardsData>('/cards', { month: period.month }),
  })

  if (isLoading) return <Loading what="cards" />
  if (error || !data) return <ErrorBox error={error} />
  if (data.cards.length === 0) return <Empty>No credit cards yet. Add one with `tideline setup`.</Empty>

  const card = data.cards.find((c) => c.accountId === selected) ?? data.cards[0]

  return (
    <>
      <h1>Credit cards</h1>
      <div className="grid cols-3">
        {data.cards.map((c) => (
          <button key={c.accountId} type="button" className={`tile ${c.accountId === card.accountId ? 'on' : ''}`} onClick={() => setSelected(c.accountId)}>
            <span className="muted">{c.name}</span>
            <span className="big">
              <Money cents={c.balanceCents} drill={c.balanceDrill} />
            </span>
            <span>
              {!c.isPayInFull ? (
                <Badge tone="muted">Not a pay-in-full card</Badge>
              ) : c.lastCheckedPaidInFull === null ? (
                <Badge tone="muted">Not checked yet</Badge>
              ) : c.lastCheckedPaidInFull ? (
                <Badge tone="good">Paid in full</Badge>
              ) : (
                <Badge tone="bad">Not paid in full</Badge>
              )}
            </span>
            <span className="cell-note">{c.nextDue ? `Next due ${dateLabel(c.nextDue)}` : 'Due day not set'}</span>
          </button>
        ))}
      </div>

      <Card title={`${card.name}, ${data.year}`} aside="Purchases against payments">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Month</th>
                <th className="num">Purchases</th>
                <th className="num">Payments</th>
                <th className="num">Balance at month end</th>
                <th>Paid in full</th>
              </tr>
            </thead>
            <tbody>
              {card.months.map((m) => (
                <tr key={m.month}>
                  <td>{shortMonth(m.month)}</td>
                  <td className="num">{m.purchasesCents ? <Money cents={-m.purchasesCents} drill={`${m.drill}&direction=out`} /> : <span className="muted">–</span>}</td>
                  <td className="num">{m.paymentsCents ? <Money cents={m.paymentsCents} drill={`${m.drill}&direction=in`} /> : <span className="muted">–</span>}</td>
                  <td className="num">{m.endBalanceCents === null ? <span className="muted">–</span> : <Money cents={m.endBalanceCents} />}</td>
                  <td>
                    {m.paidInFull === null ? (
                      <span className="muted">–</span>
                    ) : m.paidInFull ? (
                      <Badge tone="good">Yes</Badge>
                    ) : (
                      <Badge tone="bad">No</Badge>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted" style={{ marginTop: 8 }}>
          A month counts as paid in full when its end balance is covered by payments by the next month's due day{card.dueDay ? ` (the ${card.dueDay}th)` : ''}. Payments are the card side of transfers from your accounts. <Link to={`/lines?${card.paymentsDrill}`}>See this year's payments</Link>.
        </p>
      </Card>

      <Card title={`Where ${card.name} money went, ${monthLabel(data.month)}`}>
        {card.categories.length === 0 ? (
          <p className="muted">No purchases this month.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Category</th>
                  <th className="num">Lines</th>
                  <th className="num">Spent</th>
                </tr>
              </thead>
              <tbody>
                {card.categories.map((c) => (
                  <tr key={c.categoryId}>
                    <td>{c.name}</td>
                    <td className="num">{c.lines}</td>
                    <td className="num">
                      <Money cents={c.cents} drill={c.drill} />
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
