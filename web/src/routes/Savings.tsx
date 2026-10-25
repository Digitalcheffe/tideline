import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Spark } from '../components'
import { dollars, monthLabel, shortMonth } from '../format'
import { bpsText, type SavingsData } from '../phase4'
import { PlanCard } from './Planning'

export function Savings() {
  const { data, error, isLoading } = useQuery({ queryKey: ['savings'], queryFn: () => get<SavingsData>('/savings') })

  if (isLoading) return <Loading what="savings" />
  if (error || !data) return <ErrorBox error={error} />

  return (
    <>
      <h1>Savings</h1>
      <div className="grid cols-3">
        <Card title="In savings">
          <span className="big">{dollars(data.totalBalanceCents)}</span>
        </Card>
        <Card title="Going in each month">
          <span className="big">{dollars(data.totalAverageMonthlyDepositCents)}</span>
          <div className="muted">average of the last six whole months</div>
        </Card>
      </div>

      {data.accounts.length === 0 ? (
        <Card title="Accounts">
          <Empty>No savings accounts yet. Add one in Settings, Money accounts, with the kind Savings.</Empty>
        </Card>
      ) : (
        data.accounts.map((a) => (
          <Card key={a.accountId} title={a.name} aside={<Link to={`/lines?account=${a.accountId}&move=include`}>Lines</Link>}>
            <div className="grid cols-3">
              <div>
                <div className="muted">Balance</div>
                <span className="big">{dollars(a.balanceCents)}</span>
              </div>
              <div>
                <div className="muted">Pays</div>
                {a.rateBps === null ? (
                  <span className="muted">no rate entered (Settings, Money accounts)</span>
                ) : (
                  <span>
                    {bpsText(a.rateBps)} a year, about {dollars(a.interestPerMonthCents)} a month
                  </span>
                )}
              </div>
              <div>
                <div className="muted">Going in each month</div>
                <span className="big">{dollars(a.averageMonthlyDepositCents)}</span>
              </div>
            </div>
            <Spark values={a.deposits.map((d) => d.cents)} label={`${a.name} deposits by month`} />
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    {a.deposits.map((d) => (
                      <th key={d.month} className="num">
                        {shortMonth(d.month)}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    {a.deposits.map((d) => (
                      <td key={d.month} className="num">
                        {d.drill ? <Link to={`/lines?${d.drill}`}>{dollars(d.cents)}</Link> : <span className="muted">–</span>}
                      </td>
                    ))}
                  </tr>
                </tbody>
              </table>
            </div>
          </Card>
        ))
      )}

      <Card title="Plans this money is for" aside={<Link to="/planning">Planning</Link>}>
        {data.goals.length === 0 ? (
          <Empty>No plans yet.</Empty>
        ) : (
          data.goals.map((g) => (
            <div key={g.plan.id}>
              <PlanCard plan={g.plan} />
              {g.projectedAtTargetCents !== null && (
                <p>
                  At the current pace and the account&rsquo;s rate, the balance reaches <strong>{dollars(g.projectedAtTargetCents)}</strong> by {monthLabel(g.plan.targetOn.slice(0, 7))}.{' '}
                  {g.onPace ? <Badge tone="good">enough</Badge> : <Badge tone="warn">short of the target</Badge>}
                </p>
              )}
            </div>
          ))
        )}
      </Card>
    </>
  )
}
