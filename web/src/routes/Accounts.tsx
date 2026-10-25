import { useQuery } from '@tanstack/react-query'
import { get, type AccountsByMonth } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money } from '../components'
import { shortMonth } from '../format'
import type { Period } from '../period'

export function Accounts({ period }: { period: Period }) {
  const { data, error, isLoading } = useQuery({
    queryKey: ['accounts-by-month', period.year],
    queryFn: () => get<AccountsByMonth>('/accounts/by-month', { year: String(period.year) }),
  })

  if (isLoading) return <Loading what="accounts" />
  if (error || !data) return <ErrorBox error={error} />
  if (data.accounts.length === 0) return <Empty>No money accounts yet. Add them with `tideline setup`.</Empty>

  return (
    <>
      <h1>Accounts by month, {data.year}</h1>
      <p className="muted">Each cell shows money in, money out and the balance at month end. Click a number to see its lines.</p>
      {data.accounts.map((a) => (
        <Card key={a.accountId} title={a.name} aside={a.kind.replace('_', ' ')}>
          {!a.openingKnown && (
            <p>
              <Badge tone="warn">Opening balance unknown</Badge> <span className="muted">Balances count from the first import.</span>
            </p>
          )}
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="sticky-col" />
                  {a.cells.map((c) => (
                    <th key={c.month} className="num">
                      {shortMonth(c.month)}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                <tr>
                  <th className="sticky-col">In</th>
                  {a.cells.map((c) => (
                    <td key={c.month} className="num">
                      {c.inCents ? <Money cents={c.inCents} drill={`${c.drill}&direction=in`} /> : <span className="muted">–</span>}
                    </td>
                  ))}
                </tr>
                <tr>
                  <th className="sticky-col">Out</th>
                  {a.cells.map((c) => (
                    <td key={c.month} className="num">
                      {c.outCents ? <Money cents={-c.outCents} drill={`${c.drill}&direction=out`} /> : <span className="muted">–</span>}
                    </td>
                  ))}
                </tr>
                <tr className="total">
                  <th className="sticky-col">Balance</th>
                  {a.cells.map((c) => (
                    <td key={c.month} className="num">
                      {c.endBalanceCents === null ? <span className="muted">–</span> : <Money cents={c.endBalanceCents} drill={c.drill} title="Show this month's lines" />}
                    </td>
                  ))}
                </tr>
              </tbody>
            </table>
          </div>
        </Card>
      ))}
    </>
  )
}
