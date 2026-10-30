import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get, type Income as IncomeData } from '../api'
import { Badge, Card, ErrorBox, Loading, Money } from '../components'
import { dateLabel, dollars, plural, shortMonth } from '../format'
import type { Period } from '../period'

export function Income({ period }: { period: Period }) {
  const { data, error, isLoading } = useQuery({
    queryKey: ['income', period.year],
    queryFn: () => get<IncomeData>('/income', { year: String(period.year) }),
  })

  if (isLoading) return <Loading what="income" />
  if (error || !data) return <ErrorBox error={error} />

  const review = data.reviewDeposits

  return (
    <>
      <h1>Income, {data.year}</h1>
      <div className="grid cols-3">
        <Card title="Received so far">
          <span style={{ fontSize: '1.6rem', fontWeight: 650 }}>
            <Money cents={data.total.cents} drill={data.total.drill} />
          </span>
        </Card>
        <Card title="Expected for the year">
          {data.expectedCents === null ? (
            <p className="muted">No budget yet. Expected income is set on the Budget screen (Phase 4).</p>
          ) : (
            <span style={{ fontSize: '1.6rem', fontWeight: 650 }}>{dollars(data.expectedCents)}</span>
          )}
        </Card>
      </div>

      <Card title="By month">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th />
                {data.months.map((m) => (
                  <th key={m.month} className="num">
                    {shortMonth(m.month)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              <tr>
                <th scope="row">Received</th>
                {data.months.map((m) => (
                  <td key={m.month} className="num">
                    {m.actualCents ? <Money cents={m.actualCents} drill={m.drill} /> : <span className="muted">–</span>}
                  </td>
                ))}
              </tr>
              {data.expectedCents !== null && (
                <tr>
                  <th scope="row">Expected</th>
                  {data.months.map((m) => (
                    <td key={m.month} className="num muted">
                      {m.expectedCents === null ? '–' : dollars(m.expectedCents)}
                    </td>
                  ))}
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </Card>

      <div className="grid cols-2" style={{ marginTop: 12 }}>
        <Card title="By source">
          {data.categories.length === 0 ? (
            <p className="muted">No income yet.</p>
          ) : (
            <table>
              <tbody>
                {data.categories.map((c) => (
                  <tr key={c.id}>
                    <td>{c.name}</td>
                    <td className="num muted">{plural(c.lines, 'line')}</td>
                    <td className="num">
                      <Money cents={c.cents} drill={c.drill} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Card>
        <Card title="By payer">
          {data.payees.length === 0 ? (
            <p className="muted">No income yet.</p>
          ) : (
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
          )}
        </Card>
      </div>

      <Card
        title="Deposits to take a look at"
        aside={
          <Link to={`/lines?${review.drill}`}>
            {review.count} {review.count === 1 ? 'deposit' : 'deposits'}, {dollars(review.sumCents)}
          </Link>
        }
      >
        {review.items.length === 0 ? (
          <p>
            <Badge tone="good">All recognised</Badge> <span className="muted">Every deposit matched a rule.</span>
          </p>
        ) : (
          <table>
            <tbody>
              {review.items.map((l) => (
                <tr key={`${l.transactionId}-${l.splitLineId}`}>
                  <td>{dateLabel(l.postedOn)}</td>
                  <td>{l.description}</td>
                  <td className="muted">{l.accountName}</td>
                  <td className="num">
                    <Money cents={l.amountCents} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {data.taxes.length > 0 && <TaxesByYear taxes={data.taxes} />}
    </>
  )
}

/** Taxes by year: what was paid, property tax, and what came back, as bars. Each figure opens its lines. Paycheck withholding is not in the bank data. */
function TaxesByYear({ taxes }: { taxes: IncomeData['taxes'] }) {
  const biggest = Math.max(1, ...taxes.flatMap((t) => [t.paidCents, t.propertyCents, t.refundedCents]))
  const bar = (cents: number, className: string) => (
    <span className={`tax-bar ${className}`} style={{ width: `${Math.max(cents > 0 ? 2 : 0, (cents / biggest) * 100)}%` }} aria-hidden="true" />
  )
  return (
    <Card title="Taxes by year" aside={<span className="muted">payments and refunds in the bank data</span>}>
      <p className="muted">
        What was paid in taxes, property tax, and what came back as a refund, for each year. Tax taken out of a paycheck before it reaches the bank is not shown here.
      </p>
      <table className="tax-years">
        <tbody>
          {taxes.map((t) => (
            <tr key={t.year}>
              <th scope="row">{t.year}</th>
              <td className="tax-bars">
                <div>{bar(t.paidCents, 'paid')} {t.paidDrill ? <Money cents={t.paidCents} drill={t.paidDrill} /> : <span className="muted">no taxes paid</span>}</div>
                <div>{bar(t.propertyCents, 'property')} {t.propertyDrill ? <Money cents={t.propertyCents} drill={t.propertyDrill} /> : <span className="muted">no property tax</span>} <span className="muted">property tax</span></div>
                <div>{bar(t.refundedCents, 'refund')} {t.refundedDrill ? <Money cents={t.refundedCents} drill={t.refundedDrill} /> : <span className="muted">no refund</span>} <span className="muted">refunded</span></div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  )
}
