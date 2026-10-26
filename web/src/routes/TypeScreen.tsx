import { useQuery } from '@tanstack/react-query'
import { get, type TypeScreenData } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money, Spark } from '../components'
import { dateLabel, dollars, monthRange, plural } from '../format'
import type { Period } from '../period'

/** Required and Extra share one layout (docs/ARCHITECTURE.md). */
export function TypeScreen({ type, title, period }: { type: 'required' | 'extra'; title: string; period: Period }) {
  const { data, error, isLoading } = useQuery({
    queryKey: [type, period.from, period.to],
    queryFn: () => get<TypeScreenData>(`/${type}`, { from: period.from, to: period.to }),
  })

  if (isLoading) return <Loading what={title.toLowerCase()} />
  if (error || !data) return <ErrorBox error={error} />

  // months in the period, so each sparkline lines up
  const months = monthsBetween(period.from, period.to)

  return (
    <>
      <h1>{title}</h1>
      <Card title={`Total ${title.toLowerCase()}`} aside={`${data.from} to ${data.to}`}>
        <span style={{ fontSize: '1.6rem', fontWeight: 650 }}>
          <Money cents={data.total.cents} drill={data.total.drill} />
        </span>
      </Card>

      <Card title="By category">
        {data.categories.length === 0 ? (
          <Empty>No {title.toLowerCase()} lines in this period.</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Category</th>
                  <th>By month</th>
                  <th className="num">Lines</th>
                  <th className="num">Spent</th>
                </tr>
              </thead>
              <tbody>
                {data.categories.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td>
                      <Spark values={months.map((m) => c.months.find((x) => x.month === m)?.cents ?? 0)} label={`${c.name} by month`} />
                    </td>
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

      {data.subscriptions && (
        <Card title="Subscriptions" aside="Recurring items that are not bills">
          {data.subscriptions.length === 0 ? (
            <p className="muted">No subscriptions set up. Add recurring items in an extra category.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th className="num">Last charge</th>
                    <th className="num">Per month</th>
                    <th className="num">Per year</th>
                    <th>Next charge</th>
                  </tr>
                </thead>
                <tbody>
                  {data.subscriptions.map((s) => (
                    <tr key={s.id}>
                      <td>
                        {s.name} {s.priceChanged && <Badge tone="warn">Price changed</Badge>}
                      </td>
                      <td className="num">
                        {s.lastChargeCents === null ? <span className="muted">–</span> : <Money cents={-s.lastChargeCents} drill={s.drill} />}
                        {s.priceChanged && s.previousChargeCents !== null && <span className="cell-note">was {dollars(s.previousChargeCents)}</span>}
                      </td>
                      <td className="num">{dollars(s.monthlyCostCents)}</td>
                      <td className="num">{dollars(s.yearlyCostCents)}</td>
                      <td>{s.nextCharge ? dateLabel(s.nextCharge) : <span className="muted">–</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}

      <Card title="Top payees">
        {data.payees.length === 0 ? (
          <p className="muted">No payees yet.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <tbody>
                {data.payees.map((p) => (
                  <tr key={p.payee}>
                    <td>{p.payee}</td>
                    <td className="num muted">{plural(p.lines, 'line')}</td>
                    <td className="num">
                      <Money cents={p.cents} drill={p.drill} />
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

function monthsBetween(from: string, to: string): string[] {
  const out: string[] = []
  let [y, m] = from.slice(0, 7).split('-').map(Number)
  const end = to.slice(0, 7)
  for (let guard = 0; guard < 600; guard++) {
    const ym = `${y}-${String(m).padStart(2, '0')}`
    out.push(ym)
    if (ym >= end) break
    m += 1
    if (m > 12) {
      m = 1
      y += 1
    }
  }
  return out.length ? out : [monthRange(from.slice(0, 7)).from.slice(0, 7)]
}

