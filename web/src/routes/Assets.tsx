import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money, Spark } from '../components'
import { dateLabel, dollars } from '../format'

interface AssetData {
  assetId: number
  name: string
  isClosed: boolean
  valueCents: number | null
  valueAsOf: string | null
  valueStale: boolean
  owedCents: number
  equityCents: number | null
  loans: { loanId: number; name: string; owedCents: number | null; asOf: string | null }[]
  putInCents: number
  lines: number
  drill: string | null
  firstValueCents: number | null
  firstValueAsOf: string | null
  valueChangeCents: number | null
  netCents: number | null
  byYear: { year: string; putInCents: number; lines: number; drill: string }[]
  byCategory: { categoryId: number | null; name: string; putInCents: number; lines: number; drill: string }[]
  values: { asOf: string; cents: number; source: string }[]
}

/** Asset ledgers (docs/08-screens.md, "Assets"): real numbers on top, lifetime value below. */
export function Assets() {
  const { data, error, isLoading } = useQuery({ queryKey: ['assets'], queryFn: () => get<AssetData[]>('/assets') })

  if (isLoading) return <Loading what="assets" />
  if (error || !data) return <ErrorBox error={error} />

  return (
    <>
      <h1>Assets</h1>
      <p className="muted">
        A house, a car, anything you own that costs money to keep. Link a line to an asset from the Ledger (open the line, then pick the asset) and it is
        counted here. Add the asset itself in Settings, Money accounts, with the kind Asset, and enter what it is worth under Net worth on the Overview.
      </p>
      {data.length === 0 && (
        <Card>
          <Empty>No assets yet.</Empty>
        </Card>
      )}
      {data.map((a) => (
        <AssetCard key={a.assetId} a={a} />
      ))}
    </>
  )
}

function AssetCard({ a }: { a: AssetData }) {
  return (
    <Card title={a.name} aside={a.isClosed ? <Badge tone="muted">Closed</Badge> : a.drill ? <Link to={`/lines?${a.drill}`}>All lines</Link> : null}>
      <h3>Right now</h3>
      <div className="grid cols-3">
        <div>
          <div className="muted">Worth</div>
          {a.valueCents === null ? (
            <span className="muted">no figure yet. Enter what it is worth under Net worth on the Overview.</span>
          ) : (
            <>
              <span className="big">{dollars(a.valueCents)}</span>
              <div className="muted">
                as of {dateLabel(a.valueAsOf!)} {a.valueStale && <Badge tone="warn">old estimate</Badge>}
              </div>
            </>
          )}
        </div>
        <div>
          <div className="muted">Owed on it</div>
          <span className="big">{dollars(a.owedCents)}</span>
          <div className="muted">
            {a.loans.length === 0 ? 'no loan is tied to it' : a.loans.map((l) => `${l.name} ${l.owedCents === null ? '(no figure)' : dollars(l.owedCents)}`).join(', ')}
          </div>
        </div>
        <div>
          <div className="muted">Equity</div>
          {a.equityCents === null ? <span className="muted">needs a worth figure</span> : <span className="big">{dollars(a.equityCents)}</span>}
          <div className="muted">worth less what is owed</div>
        </div>
      </div>

      <h3>Over its whole life</h3>
      {a.lines === 0 ? (
        <p className="muted">Nothing is linked to it yet, so there is nothing put in to compare.</p>
      ) : (
        <>
          <div className="grid cols-3">
            <div>
              <div className="muted">Put in</div>
              <span className="big">
                <Money cents={a.putInCents} drill={a.drill} />
              </span>
              <div className="muted">
                {a.lines} {a.lines === 1 ? 'line' : 'lines'}, refunds taken off
              </div>
            </div>
            <div>
              <div className="muted">Worth now</div>
              {a.valueCents === null ? <span className="muted">no figure</span> : <span className="big">{dollars(a.valueCents)}</span>}
            </div>
            <div>
              <div className="muted">Change in worth</div>
              {a.valueChangeCents === null ? (
                <span className="muted">needs two figures</span>
              ) : (
                <>
                  <span className="big">
                    <Money cents={a.valueChangeCents} />
                  </span>
                  <div className="muted">since {dateLabel(a.firstValueAsOf!)} ({dollars(a.firstValueCents!)})</div>
                </>
              )}
            </div>
          </div>
          {a.netCents !== null && (
            <p>
              Counting what went into it, it is{' '}
              <strong>
                <Money cents={Math.abs(a.netCents)} />
              </strong>{' '}
              {a.netCents >= 0 ? 'better off' : 'worse off'} than when it was first valued.
            </p>
          )}

          <div className="grid cols-2">
            <div className="table-wrap">
              <table>
                <caption>Put in by year</caption>
                <tbody>
                  {a.byYear.map((y) => (
                    <tr key={y.year}>
                      <td>{y.year}</td>
                      <td className="num">
                        <Money cents={y.putInCents} drill={y.drill} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="table-wrap">
              <table>
                <caption>Put in by category</caption>
                <tbody>
                  {a.byCategory.map((c) => (
                    <tr key={c.categoryId ?? 0}>
                      <td>{c.name}</td>
                      <td className="num">
                        <Money cents={c.putInCents} drill={c.drill} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}

      {a.values.length > 1 && (
        <>
          <h3>Worth over time</h3>
          <Spark values={a.values.map((v) => v.cents)} label={`${a.name} value over time`} />
        </>
      )}
    </Card>
  )
}
