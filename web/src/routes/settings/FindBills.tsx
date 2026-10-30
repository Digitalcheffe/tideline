import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get, send } from '../../api'
import { Badge, Card } from '../../components'
import { dateLabel, dollars } from '../../format'
import { Problem, useAct } from './common'

export interface Found {
  /** a large number, so it travels as text */
  key: string
  name: string
  categoryName: string
  kind: 'bill' | 'subscription'
  frequency: 'monthly' | 'yearly'
  everyN: number
  minCents: number
  maxCents: number
  dueDay: number
  occurrences: number
  lastPaid: string
  monthlyCents: number
  lines: number
  linksNextOnes: boolean
  drill: string
}

export const candidatesQuery = { queryKey: ['recurring-candidates'], queryFn: () => get<Found[]>('/recurring/candidates') }

function when(f: Found): string {
  if (f.frequency === 'yearly') return 'once a year'
  return f.everyN === 1 ? `monthly, about the ${f.dueDay}${suffix(f.dueDay)}` : `every ${f.everyN} months`
}

function suffix(n: number): string {
  if (n % 100 >= 11 && n % 100 <= 13) return 'th'
  return ['th', 'st', 'nd', 'rd'][n % 10 > 3 ? 0 : n % 10] ?? 'th'
}

/**
 * Find my bills: what Tideline found in lines that are not tracked yet. Tick the ones to track (rename any first) and they become recurring items,
 * their past lines are linked, and next month's link themselves. Nothing is added until you press the button.
 */
export function FindBills() {
  const found = useQuery(candidatesQuery)
  const { run, error, busy } = useAct()
  const [ticked, setTicked] = useState<Set<string> | null>(null)
  const [names, setNames] = useState<Record<string, string>>({})
  const [result, setResult] = useState<string | null>(null)

  const list = found.data ?? []
  const chosen = ticked ?? new Set(list.map((f) => f.key)) // everything starts ticked
  if (list.length === 0 && result === null) return null

  const toggle = (key: string) => {
    const next = new Set(chosen)
    if (next.has(key)) next.delete(key)
    else next.add(key)
    setTicked(next)
  }

  const add = async () => {
    const items = list.filter((f) => chosen.has(f.key)).map((f) => ({ key: f.key, name: names[f.key]?.trim() || undefined }))
    const done = await run(() => send<{ added: { name: string; linesLinked: number }[] }>('POST', '/recurring/candidates/add', { items }))
    if (done) {
      setResult(`Added ${done.added.length} ${done.added.length === 1 ? 'item' : 'items'} and linked ${done.added.reduce((n, a) => n + a.linesLinked, 0)} lines.`)
      setTicked(null)
      setNames({})
    }
  }

  const bills = list.filter((f) => f.kind === 'bill')
  const subs = list.filter((f) => f.kind === 'subscription')

  return (
    <Card title="Found in your lines">
      {result && (
        <p>
          <Badge tone="good">Done</Badge> {result}
        </p>
      )}
      {list.length > 0 && (
        <>
          <p className="muted">
            These payments repeat on a pattern but are not tracked yet. Tracking one gives it a row on the Bills screen (or the subscriptions list), notices a missed payment, links its past
            lines and links next month's by itself. Untick any you do not want, and rename any before adding.
          </p>
          {[
            ['Bills', bills],
            ['Subscriptions', subs],
          ].map(
            ([title, rows]) =>
              (rows as Found[]).length > 0 && (
                <div key={title as string}>
                  <h3>{title as string}</h3>
                  <div className="table-wrap">
                    <table>
                      <thead>
                        <tr>
                          <th />
                          <th>Name</th>
                          <th>Category</th>
                          <th>How often</th>
                          <th className="num">Usual amount</th>
                          <th className="num">Seen</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(rows as Found[]).map((f) => (
                          <tr key={f.key}>
                            <td>
                              <input type="checkbox" aria-label={`Track ${f.name}`} checked={chosen.has(f.key)} onChange={() => toggle(f.key)} />
                            </td>
                            <td>
                              <input
                                type="text"
                                aria-label={`Name for ${f.name}`}
                                value={names[f.key] ?? f.name}
                                maxLength={100}
                                onChange={(e) => setNames({ ...names, [f.key]: e.target.value })}
                              />
                              <div className="muted">
                                <Link to={`/lines?${f.drill}`}>Show the lines</Link>
                                {!f.linksNextOnes && ' · next ones will not link by themselves'}
                              </div>
                            </td>
                            <td>{f.categoryName}</td>
                            <td>{when(f)}</td>
                            <td className="num">
                              {dollars(f.minCents)} to {dollars(f.maxCents)}
                            </td>
                            <td className="num">
                              {f.occurrences}×<div className="muted">last {dateLabel(f.lastPaid)}</div>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              ),
          )}
          <div className="toolbar">
            <button className="btn primary" type="button" disabled={busy || chosen.size === 0} onClick={add}>
              {busy ? 'Adding…' : `Track ${chosen.size} selected`}
            </button>
          </div>
        </>
      )}
      <Problem message={error} />
    </Card>
  )
}
