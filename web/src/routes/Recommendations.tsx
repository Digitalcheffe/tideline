import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { get, send } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading } from '../components'

interface RecoCard {
  ruleId: number
  ruleName: string
  suggestion: string
  flagId: number
  flagKind: string
  severity: 'alert' | 'warn' | 'info'
  flagTitle: string
  flagDetail: string
  acknowledged: boolean
  what: string
  link: string
}

interface RecoRule {
  id: number
  name: string
  flagKind: string
  minSeverity: string
  contains: string | null
  suggestion: string
  isActive: boolean
}

interface RuleData {
  kinds: { kind: string; label: string }[]
  rules: RecoRule[]
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
      severity: list.reduce((worst, c) => (rank[c.severity] > rank[worst] ? c.severity : worst), list[0].severity),
      cards: list,
    }))
    .sort((a, b) => rank[b.severity] - rank[a.severity] || b.cards.length - a.cards.length)
}

const TONE = { alert: 'bad', warn: 'warn', info: 'info' } as const
const SEVERITY = { alert: 'Needs you', warn: 'Worth a look', info: 'For your information' } as const

/** Suggestions: a kind of flag, how serious, and some words produce a card with what to try (docs/ARCHITECTURE.md). */
export function Recommendations() {
  const client = useQueryClient()
  const cards = useQuery({ queryKey: ['recommendations'], queryFn: () => get<RecoCard[]>('/recommendations') })
  const dismiss = useMutation({
    mutationFn: (c: RecoCard) => send('POST', '/recommendations/dismiss', { ruleId: c.ruleId, flagId: c.flagId }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['recommendations'] }),
  })

  return (
    <>
      <h1>Recommendations</h1>
      <p className="muted">
        Ideas for what to try, shown while something is flagged. A suggestion goes away when the flag clears. These are plain suggestions from rules you can
        change below; they are not financial advice.
      </p>

      {cards.isLoading && <Loading what="recommendations" />}
      {cards.error && <ErrorBox error={cards.error} />}
      {cards.data && cards.data.length === 0 && (
        <Card>
          <Empty>Nothing to suggest right now. Nothing is flagged that has a suggestion.</Empty>
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
                    Take a look
                  </Link>
                  <button className="btn" type="button" disabled={dismiss.isPending} onClick={() => dismiss.mutate(c)} title="Hide this suggestion for this flag">
                    Not useful here
                  </button>
                </span>
              </li>
            ))}
          </ul>
        </Card>
      ))}
      {dismiss.error && <p className="problem" role="alert">{dismiss.error.message}</p>}

      <Rules />
    </>
  )
}

function Rules() {
  const client = useQueryClient()
  const data = useQuery({ queryKey: ['recommendation-rules'], queryFn: () => get<RuleData>('/recommendation-rules') })
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const refresh = () => {
    client.invalidateQueries({ queryKey: ['recommendation-rules'] })
    client.invalidateQueries({ queryKey: ['recommendations'] })
  }
  const toggle = useMutation({
    mutationFn: (r: RecoRule) => send('PATCH', `/recommendation-rules/${r.id}`, { isActive: !r.isActive }),
    onSuccess: refresh,
  })
  const remove = useMutation({ mutationFn: (r: RecoRule) => send('DELETE', `/recommendation-rules/${r.id}`), onSuccess: refresh })

  if (data.isLoading) return <Loading what="the suggestion rules" />
  if (data.error || !data.data) return <ErrorBox error={data.error} />
  const label = (kind: string) => data.data.kinds.find((k) => k.kind === kind)?.label ?? kind

  return (
    <Card
      title="The suggestions"
      aside={
        <button className="btn" type="button" onClick={() => setEditing('new')}>
          Add a suggestion
        </button>
      }
    >
      {editing === 'new' && <RuleForm kinds={data.data.kinds} onDone={() => { setEditing(null); refresh() }} onCancel={() => setEditing(null)} />}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>On</th>
              <th>When</th>
              <th>Suggests</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {data.data.rules.map((r) =>
              editing === r.id ? (
                <tr key={r.id}>
                  <td colSpan={4}>
                    <RuleForm rule={r} kinds={data.data.kinds} onDone={() => { setEditing(null); refresh() }} onCancel={() => setEditing(null)} />
                  </td>
                </tr>
              ) : (
                <tr key={r.id} className={r.isActive ? '' : 'muted'}>
                  <td>
                    <input type="checkbox" aria-label={`Use ${r.name}`} checked={r.isActive} disabled={toggle.isPending} onChange={() => toggle.mutate(r)} />
                  </td>
                  <td>
                    <strong>{r.name}</strong>
                    <div className="muted">
                      {label(r.flagKind)}
                      {r.minSeverity !== 'info' && `, at least ${r.minSeverity}`}
                      {r.contains && `, mentioning “${r.contains}”`}
                    </div>
                  </td>
                  <td>{r.suggestion}</td>
                  <td>
                    <button className="btn" type="button" onClick={() => setEditing(r.id)}>
                      Edit
                    </button>{' '}
                    <button
                      className="btn"
                      type="button"
                      disabled={remove.isPending}
                      onClick={() => window.confirm(`Delete “${r.name}”?`) && remove.mutate(r)}
                    >
                      Delete
                    </button>
                  </td>
                </tr>
              ),
            )}
          </tbody>
        </table>
      </div>
      {(toggle.error || remove.error) && <p className="problem" role="alert">{String((toggle.error ?? remove.error)?.message)}</p>}
    </Card>
  )
}

function RuleForm({ rule, kinds, onDone, onCancel }: { rule?: RecoRule; kinds: RuleData['kinds']; onDone: () => void; onCancel: () => void }) {
  const [name, setName] = useState(rule?.name ?? '')
  const [flagKind, setFlagKind] = useState(rule?.flagKind ?? kinds[0]?.kind ?? '')
  const [minSeverity, setMinSeverity] = useState(rule?.minSeverity ?? 'info')
  const [contains, setContains] = useState(rule?.contains ?? '')
  const [suggestion, setSuggestion] = useState(rule?.suggestion ?? '')

  const save = useMutation({
    mutationFn: () => {
      const body = { name, flagKind, minSeverity, contains, suggestion }
      return rule ? send('PATCH', `/recommendation-rules/${rule.id}`, body) : send('POST', '/recommendation-rules', body)
    },
    onSuccess: onDone,
  })

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      <div className="form">
        <label>
          Name
          <input type="text" value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          When this is flagged
          <select value={flagKind} onChange={(e) => setFlagKind(e.target.value)}>
            {kinds.map((k) => (
              <option key={k.kind} value={k.kind}>
                {k.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          At least this serious
          <select value={minSeverity} onChange={(e) => setMinSeverity(e.target.value)}>
            <option value="info">Any</option>
            <option value="warn">Worth a look or worse</option>
            <option value="alert">Needs you</option>
          </select>
        </label>
        <label>
          Only if the flag mentions <span className="muted">(optional)</span>
          <input type="text" value={contains} onChange={(e) => setContains(e.target.value)} />
        </label>
        <label>
          Suggest
          <textarea rows={3} value={suggestion} onChange={(e) => setSuggestion(e.target.value)} />
        </label>
      </div>
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button className="btn primary" type="submit" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
      {save.error && <p className="problem" role="alert">{save.error.message}</p>}
    </form>
  )
}
