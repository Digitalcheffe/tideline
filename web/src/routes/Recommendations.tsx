import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { get, send } from '../api'
import { useMe } from '../auth'
import { Badge, Card, Empty, ErrorBox, Loading } from '../components'

interface RecoCard {
  ruleId: number
  ruleName: string
  suggestion: string
  note: string | null
  action: string | null
  flagId: number
  flagKind: string
  severity: 'alert' | 'warn' | 'info'
  flagTitle: string
  flagDetail: string
  acknowledged: boolean
  what: string
  link: string
}

interface Suggestion {
  id: number
  key: string
  group: string
  title: string
  advice: string
  note: string | null
  recurrence: string
  isActive: boolean
  /** true when the suggestion raises its own card and so has its own message; null for advice attached to an alert */
  notify: boolean | null
}

/** One card per suggestion, however many flags it fits: ten unpaid bills are one suggestion with ten reasons. Most serious first. */
function groups(cards: RecoCard[]) {
  const rank = { alert: 2, warn: 1, info: 0 }
  const byRule = new Map<number, RecoCard[]>()
  for (const c of cards) byRule.set(c.ruleId, [...(byRule.get(c.ruleId) ?? []), c])
  return [...byRule.values()]
    .map((list) => ({
      ruleId: list[0].ruleId,
      ruleName: list[0].ruleName,
      suggestion: list[0].suggestion,
      note: list[0].note,
      severity: list.reduce((worst, c) => (rank[c.severity] > rank[worst] ? c.severity : worst), list[0].severity),
      cards: list,
    }))
    .sort((a, b) => rank[b.severity] - rank[a.severity] || b.cards.length - a.cards.length)
}

/** The fixed steps a card can offer. Only a click runs one. */
const ACTIONS: Record<string, string> = { mark_one_time: 'Mark as One-time', add_recurring: 'Yes, add it' }

const TONE = { alert: 'bad', warn: 'warn', info: 'info' } as const
const SEVERITY = { alert: 'Needs you', warn: 'Worth a look', info: 'For your information' } as const

/** Suggestions: prompts to look at something, shipped with the app. Switch each on or off; mark a card reviewed when you have looked. */
export function Recommendations() {
  const client = useQueryClient()
  const cards = useQuery({ queryKey: ['recommendations'], queryFn: () => get<RecoCard[]>('/recommendations') })
  const dismiss = useMutation({
    mutationFn: (c: RecoCard) => send('POST', '/recommendations/dismiss', { ruleId: c.ruleId, flagId: c.flagId }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['recommendations'] }),
  })

  const act = useMutation({
    mutationFn: (c: RecoCard) => send('POST', '/recommendations/action', { flagId: c.flagId }),
    onSuccess: () => client.invalidateQueries(),
  })

  return (
    <>
      <h1>Recommendations</h1>
      <p className="muted">
        Things worth a look, shown while they are true. Each is a prompt to look, not financial or tax advice. Press Reviewed once you have looked; it comes back
        when it next applies. Switch any suggestion off below.
      </p>

      {cards.isLoading && <Loading what="recommendations" />}
      {cards.error && <ErrorBox error={cards.error} />}
      {cards.data && cards.data.length === 0 && (
        <Card>
          <Empty>Nothing to suggest right now.</Empty>
        </Card>
      )}
      {groups(cards.data ?? []).map((g) => (
        <Card key={g.ruleId} title={g.ruleName} aside={<Badge tone={TONE[g.severity]}>{SEVERITY[g.severity]}</Badge>}>
          <p>{g.suggestion}</p>
          <p className="muted">{g.cards.length === 1 ? 'Because of:' : `Because of these ${g.cards.length}:`}</p>
          <ul className="because">
            {g.cards.map((c) => (
              <li key={c.flagId}>
                <strong>{c.flagTitle}</strong>
                {c.flagDetail && <span className="muted"> {c.flagDetail}</span>} {c.acknowledged && <Badge tone="muted">acknowledged</Badge>}
                <span className="toolbar">
                  <Link className="btn" to={c.link}>
                    Show the lines
                  </Link>
                  {c.action && ACTIONS[c.action] && (
                    <button className="btn primary" type="button" disabled={act.isPending} onClick={() => act.mutate(c)}>
                      {act.isPending ? 'Working…' : ACTIONS[c.action]}
                    </button>
                  )}
                  <button className="btn" type="button" disabled={dismiss.isPending} onClick={() => dismiss.mutate(c)} title="You have looked. It comes back when it next applies.">
                    Reviewed
                  </button>
                </span>
              </li>
            ))}
          </ul>
          {g.note && <p className="muted small-print">{g.note}</p>}
        </Card>
      ))}
      {(dismiss.error ?? act.error) && <p className="problem" role="alert">{(dismiss.error ?? act.error)!.message}</p>}

      <Suggestions />
    </>
  )
}

function Suggestions() {
  const client = useQueryClient()
  const me = useMe()
  const data = useQuery({ queryKey: ['recommendation-rules'], queryFn: () => get<{ rules: Suggestion[] }>('/recommendation-rules') })
  const refresh = () => {
    client.invalidateQueries({ queryKey: ['recommendation-rules'] })
    client.invalidateQueries({ queryKey: ['recommendations'] })
  }
  const toggle = useMutation({
    mutationFn: (r: Suggestion) => send('PATCH', `/recommendation-rules/${r.id}`, { isActive: !r.isActive }),
    onSuccess: refresh,
  })
  // sending a message is one of the owner's notification choices
  const notify = useMutation({
    mutationFn: (r: Suggestion) => send('PUT', '/notify/prefs', { modes: { [r.key]: r.notify ? 'off' : 'immediate' } }),
    onSuccess: refresh,
  })

  if (data.isLoading) return <Loading what="the suggestions" />
  if (data.error || !data.data) return <ErrorBox error={data.error} />

  const isOwner = me.user.role === 'owner'
  const order = ['Alerts']
  const byGroup = new Map<string, Suggestion[]>()
  for (const r of data.data.rules) byGroup.set(r.group, [...(byGroup.get(r.group) ?? []), r])
  const names = [...byGroup.keys()].sort((a, b) => (order.includes(a) ? 1 : 0) - (order.includes(b) ? 1 : 0))

  return (
    <>
      {names.map((name) => (
        <Card
          key={name}
          title={name === 'Alerts' ? 'Advice for the alerts' : name}
        >
          {name === 'Alerts' && <p className="muted">Shown beside the alert it goes with. Whether the alert itself is sent is set under Settings, Notifications.</p>}
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>On</th>
                  <th>Suggestion</th>
                  {name !== 'Alerts' && <th>Send a message</th>}
                </tr>
              </thead>
              <tbody>
                {byGroup.get(name)!.map((r) => (
                  <tr key={r.id} className={r.isActive ? '' : 'muted'}>
                    <td>
                      <input type="checkbox" aria-label={`Use ${r.title}`} checked={r.isActive} disabled={toggle.isPending} onChange={() => toggle.mutate(r)} />
                    </td>
                    <td>
                      <strong>{r.title}</strong>
                      <div className="muted">{r.advice}</div>
                      {name !== 'Alerts' && <div className="muted">Comes back {r.recurrence === 'each time' ? 'each time it applies' : r.recurrence}.</div>}
                    </td>
                    {name !== 'Alerts' && (
                      <td>
                        {r.notify === null ? null : (
                          <input
                            type="checkbox"
                            aria-label={`Send a message for ${r.title}`}
                            checked={r.notify}
                            disabled={!isOwner || notify.isPending}
                            title={isOwner ? undefined : 'Only an owner can change what is sent'}
                            onChange={() => notify.mutate(r)}
                          />
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      ))}
      {(toggle.error || notify.error) && <p className="problem" role="alert">{String((toggle.error ?? notify.error)?.message)}</p>}
    </>
  )
}
