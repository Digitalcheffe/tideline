import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { candidatesQuery } from './settings/FindBills'
import { get, type Bills as BillsData } from '../api'
import { Badge, Card, ErrorBox, Loading, Money } from '../components'
import { dateLabel, dollars, shortMonth } from '../format'
import type { Period } from '../period'

const STATUS: Record<string, { label: string; tone: 'good' | 'warn' | 'bad' | 'info' | 'muted' }> = {
  paid: { label: 'Paid', tone: 'good' },
  paid_unusual: { label: 'Unusual', tone: 'warn' },
  paid_extra: { label: 'Extra', tone: 'info' },
  due: { label: 'Due', tone: 'info' },
  missing: { label: 'Missing', tone: 'bad' },
  projected: { label: 'Projected', tone: 'muted' },
}

export function Bills({ period }: { period: Period }) {
  const { data, error, isLoading } = useQuery({
    queryKey: ['bills', period.year],
    queryFn: () => get<BillsData>('/bills', { year: String(period.year) }),
  })

  // bills and subscriptions found in lines that are not tracked yet (a quiet nudge to Find my bills)
  const found = useQuery(candidatesQuery)
  const untracked = found.data?.filter((f) => f.kind === 'bill').length ?? 0

  if (isLoading) return <Loading what="bills" />
  if (error || !data) return <ErrorBox error={error} />

  const months = data.columnTotals.map((c) => c.month)

  return (
    <>
      <h1>Bills, {data.year}</h1>
      <p className="muted">
        One row per recurring bill. Past months show what was paid; future months are projected from the last three payments (shown lighter). Bills with no recurring item are
        grouped as “Other” so the total matches the Bills number on the Overview.
      </p>

      {untracked > 0 && (
        <p>
          <Badge tone="info">{untracked === 1 ? '1 bill found' : `${untracked} bills found`}</Badge> that {untracked === 1 ? 'is' : 'are'} not tracked yet.{' '}
          <Link to="/settings/recurring">Find my bills</Link>
        </p>
      )}

      <Card>
        {data.rows.length === 0 ? (
          <p className="muted">No bills yet. Add recurring items with `tideline setup`, and import some exports.</p>
        ) : (
          <div className="table-wrap">
            <table className="bills-grid">
              <thead>
                <tr>
                  <th className="sticky-col">Bill</th>
                  {months.map((m) => (
                    <th key={m} className="num">
                      {shortMonth(m)}
                    </th>
                  ))}
                  <th className="num sticky-total">Total</th>
                  <th className="num sticky-average" title="The year's total divided by 12">Avg a month</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((r) => (
                  <tr key={r.recurringId ?? r.name}>
                    <th className="sticky-col" scope="row">
                      {r.name}
                    </th>
                    {r.cells.map((c) => {
                      const status = STATUS[c.status]
                      const projected = c.status === 'projected'
                      return (
                        <td key={c.month} className={`num ${projected ? 'projected' : ''}`}>
                          {c.paidCents > 0 ? (
                            <Money cents={c.paidCents} drill={c.drill} />
                          ) : projected ? (
                            <Money cents={c.projectedCents} />
                          ) : (
                            <span className="muted">–</span>
                          )}
                          {status && <span className="cell-note">{status.label}</span>}
                        </td>
                      )
                    })}
                    <td className="num sticky-total">
                      <strong>{dollars(r.totalCents)}</strong>
                      {r.totalProjectedCents > 0 && <span className="cell-note">incl. {dollars(r.totalProjectedCents)} projected</span>}
                    </td>
                    <td className="num sticky-average">{dollars(Math.round(r.totalCents / 12))}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="total">
                  <th className="sticky-col" scope="row">
                    Total
                  </th>
                  {data.columnTotals.map((c) => (
                    <td key={c.month} className={`num ${c.paidCents === 0 && c.projectedCents > 0 ? 'projected' : ''}`}>
                      {dollars(c.paidCents + c.projectedCents)}
                      {c.projectedCents > 0 && <span className="cell-note">incl. {dollars(c.projectedCents)} projected</span>}
                    </td>
                  ))}
                  <td className="num sticky-total">
                    {dollars(data.grandTotalCents)}
                    <span className="cell-note">
                      {dollars(data.totalPaidCents)} paid{data.totalProjectedCents > 0 ? ` + ${dollars(data.totalProjectedCents)} projected` : ''}
                    </span>
                  </td>
                  <td className="num sticky-average">{dollars(Math.round(data.grandTotalCents / 12))}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}
        <p style={{ marginTop: 8 }}>
          {Object.entries(STATUS).map(([k, v]) => (
            <span key={k} style={{ marginRight: 6 }}>
              <Badge tone={v.tone}>{v.label}</Badge>
            </span>
          ))}
        </p>
      </Card>

      <Card
        title="One-time items needing a look"
        aside={
          <Link to={`/lines?${data.oneTimeAttention.drill}`}>
            Over {dollars(data.oneTimeAttention.thresholdCents)}
          </Link>
        }
      >
        {data.oneTimeAttention.items.length === 0 ? (
          <p className="muted">Nothing large and one-time this year.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Payee</th>
                  <th>Category</th>
                  <th className="num">Amount</th>
                </tr>
              </thead>
              <tbody>
                {data.oneTimeAttention.items.map((l) => (
                  <tr key={`${l.transactionId}-${l.splitLineId}`}>
                    <td>{dateLabel(l.postedOn)}</td>
                    <td>{l.payee ?? l.description}</td>
                    <td>{l.categoryName ?? '–'}</td>
                    <td className="num">
                      <Money cents={l.amountCents} drill={`from=${l.postedOn}&to=${l.postedOn}&q=${encodeURIComponent(l.description)}`} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted">A payment you know is a one-off can be marked as One-time from its line, or from the large payment suggestion.</p>
      </Card>
    </>
  )
}
