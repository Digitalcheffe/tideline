import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { daysAgoLocal, dollars, todayLocal, typeLabel } from '../../format'
import { Panel, Problem, TYPE_CHOICES, useAct } from './common'

type Value = string | number | string[]

interface Condition {
  field: string
  op: string
  value: Value
}

interface RuleRow {
  id: number
  name: string
  priority: number
  isActive: boolean
  setCategoryId: number | null
  setCategoryName: string | null
  setType: string | null
  setPayee: string | null
  setRecurringId: number | null
  tags: string[]
  conditions: Condition[]
  lineCount: number
}

interface Meaning {
  categoryName: string | null
  type: string
  payee: string | null
  ruleName: string | null
  tags: string[]
}

interface TestResult {
  linesChecked: number
  wouldChange: number
  changes: { transactionId: number; postedOn: string; description: string; amountCents: number; accountName: string; before: Meaning; after: Meaning }[]
}

interface Named {
  id: number
  name: string
}

interface StarterStatus {
  categoriesInPack: number
  categoriesToAdd: number
  rulesInPack: number
  rulesToAdd: number
}

interface StarterResult {
  categoriesAdded: string[]
  rulesAdded: string[]
}

/** What a condition looks like in a sentence. */
export function describe(c: Condition): string {
  const list = (v: Value) => (Array.isArray(v) ? v.join(' or ') : String(v))
  switch (`${c.field} ${c.op}`) {
    case 'description contains_any':
      return `the description contains ${list(c.value)}`
    case 'description equals_any':
      return `the description is exactly ${list(c.value)}`
    case 'description starts_with':
      return `the description starts with ${list(c.value)}`
    case 'amount gte':
      return `the amount is at least ${dollars(Math.round(Number(c.value) * 100))}`
    case 'amount lte':
      return `the amount is at most ${dollars(Math.round(Number(c.value) * 100))}`
    case 'account is':
      return `the account is ${list(c.value)}`
    case 'account_kind in':
      return `the account kind is ${list(c.value)}`
    case 'direction is':
      return c.value === 'in' ? 'money comes in' : 'money goes out'
    default:
      return `${c.field} ${c.op} ${list(c.value)}`
  }
}

export function RulesSettings() {
  const rules = useQuery({ queryKey: ['settings', 'rules'], queryFn: () => get<RuleRow[]>('/rules') })
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<Named[]>('/categories') })
  const [params, setParams] = useSearchParams()
  const [editing, setEditing] = useState<RuleRow | 'new' | null>(params.get('new') ? 'new' : null)
  const move = useAct()
  // owners only: for anyone else the call is refused, and the section just stays out of sight
  const starter = useQuery({ queryKey: ['settings', 'rules', 'starter'], queryFn: () => get<StarterStatus>('/rules/starter'), retry: false })
  const loadStarter = useAct()
  const [loaded, setLoaded] = useState<StarterResult | null>(null)

  // "Create rule from this line" in the ledger arrives here with the line's words and what was picked already filled in
  const prefill = editing === 'new' && params.get('new') ? params : null
  const closeForm = () => {
    setEditing(null)
    if (params.get('new')) setParams({}, { replace: true })
  }

  if (rules.isLoading || categories.isLoading) return <Loading what="rules" />
  if (rules.error || !rules.data || !categories.data) return <ErrorBox error={rules.error ?? categories.error} />

  const shift = (index: number, by: number) => {
    const ids = rules.data.map((r) => r.id)
    const to = index + by
    if (to < 0 || to >= ids.length) return
    ;[ids[index], ids[to]] = [ids[to], ids[index]]
    void move.run(() => send('POST', '/rules/reorder', { ids }))
  }

  return (
    <>
      {editing && (
        <RuleForm key={editing === 'new' ? 'new' : editing.id} rule={editing === 'new' ? null : editing} prefill={prefill} categories={categories.data} onClose={closeForm} />
      )}
      <Card
        title="Rules"
        aside={
          <button type="button" className="btn primary" onClick={() => setEditing('new')}>
            Add a rule
          </button>
        }
      >
        <p className="muted">
          Rules decide what new lines mean. They are tried from the top; the first one whose conditions all hold wins. Lines you set by hand are never
          changed by a rule.
        </p>
        <Problem message={move.error} />
        {loaded && (
          <p role="status">
            Added {loaded.categoriesAdded.length} categor{loaded.categoriesAdded.length === 1 ? 'y' : 'ies'} and {loaded.rulesAdded.length} rule
            {loaded.rulesAdded.length === 1 ? '' : 's'}. They are in the audit log.
          </p>
        )}
        {starter.data && starter.data.rulesToAdd + starter.data.categoriesToAdd > 0 && (
          <section className="panel" aria-label="Starter set">
            <strong>Start from a ready-made set</strong>
            <p className="muted">
              {starter.data.categoriesToAdd} categories and {starter.data.rulesToAdd} rules for common shops, bills and the words banks use. The rules are named “Starter: …” and
              come after your own, so yours always win. Anything they cannot tell goes to review. You can change or switch off any of them.
            </p>
            <div className="toolbar">
              <button
                type="button"
                className="btn primary"
                disabled={loadStarter.busy}
                onClick={async () => {
                  if (!window.confirm(`Add ${starter.data!.categoriesToAdd} categories and ${starter.data!.rulesToAdd} rules? Nothing you made is changed.`)) return
                  const done = await loadStarter.run(() => send<StarterResult>('POST', '/rules/starter'))
                  if (done) setLoaded(done)
                }}
              >
                Add the starter set
              </button>
            </div>
            <Problem message={loadStarter.error} />
          </section>
        )}
        <RerunPanel />
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Order</th>
                <th>Rule</th>
                <th>When</th>
                <th>Then</th>
                <th className="num">Lines</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rules.data.map((r, i) => (
                <tr key={r.id}>
                  <td>
                    <button type="button" className="btn" aria-label={`Move ${r.name} up`} disabled={i === 0 || move.busy} onClick={() => shift(i, -1)}>
                      ↑
                    </button>{' '}
                    <button type="button" className="btn" aria-label={`Move ${r.name} down`} disabled={i === rules.data.length - 1 || move.busy} onClick={() => shift(i, 1)}>
                      ↓
                    </button>
                  </td>
                  <td>
                    {r.name} {!r.isActive && <Badge tone="muted">off</Badge>}
                  </td>
                  <td>{r.conditions.length === 0 ? <span className="muted">always</span> : r.conditions.map(describe).join(', and ')}</td>
                  <td>
                    {[r.setCategoryName, r.setType ? typeLabel(r.setType) : null, r.setPayee ? `payee ${r.setPayee}` : null, ...r.tags.map((t) => `tag ${t}`)]
                      .filter(Boolean)
                      .join(', ')}
                  </td>
                  <td className="num">{r.lineCount}</td>
                  <td>
                    <button type="button" className="btn" onClick={() => setEditing(r)}>
                      Change
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </>
  )
}

// ---- running every rule again -------------------------------------------------------------------------------

const SPANS: { key: string; label: string; from: () => string; to: () => string }[] = [
  { key: '90', label: 'The last 90 days', from: () => daysAgoLocal(90), to: () => todayLocal() },
  { key: '365', label: 'The last year', from: () => daysAgoLocal(365), to: () => todayLocal() },
  { key: 'all', label: 'Everything', from: () => '1900-01-01', to: () => '2999-12-31' },
]

/**
 * Rules decide what a line means when it comes in. After a rule is made or changed, this runs all of them again over lines already in the
 * ledger: look first at what would change, then do it. Lines you set by hand are skipped.
 */
function RerunPanel() {
  const [span, setSpan] = useState('365')
  const [result, setResult] = useState<TestResult | null>(null)
  const [done, setDone] = useState<number | null>(null)
  const check = useAct()
  const run = useAct()
  const client = useQueryClient()
  const chosen = SPANS.find((s) => s.key === span)!

  return (
    <details className="panel" aria-label="Run the rules again">
      <summary>
        <strong>Run all the rules again over past lines</strong>
      </summary>
      <p className="muted">
        A rule you add or change only works on new lines. This tries every rule, in order, on lines already in the ledger. Lines you set by hand, transfers and lines left out
        are skipped, and a bill link you made stays. Look at what would change first.
      </p>
      <div className="toolbar">
        <select
          aria-label="Which lines"
          value={span}
          onChange={(e) => {
            setSpan(e.target.value)
            setResult(null)
            setDone(null)
          }}
        >
          {SPANS.map((s) => (
            <option key={s.key} value={s.key}>
              {s.label}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="btn"
          disabled={check.busy}
          onClick={async () => {
            setDone(null)
            setResult((await check.run(() => send<TestResult>('POST', '/rules/rerun/test', { from: chosen.from(), to: chosen.to() }))) ?? null)
          }}
        >
          See what would change
        </button>
        {result && result.wouldChange > 0 && (
          <button
            type="button"
            className="btn primary"
            disabled={run.busy}
            onClick={async () => {
              if (!window.confirm(`Change ${result.wouldChange} lines to what the rules say now? Lines you set by hand are skipped. Each change goes in the audit log.`)) return
              const changed = await run.run(() => send<{ changed: number }>('POST', '/rules/rerun', { from: chosen.from(), to: chosen.to() }))
              if (changed) {
                setDone(changed.changed)
                setResult(null)
                void client.invalidateQueries()
              }
            }}
          >
            {run.busy ? 'Working…' : `Run them on ${result.wouldChange} ${result.wouldChange === 1 ? 'line' : 'lines'}`}
          </button>
        )}
      </div>
      <Problem message={check.error ?? run.error} />
      {done !== null && <p role="status">Changed {done} {done === 1 ? 'line' : 'lines'}. Each change is in the audit log.</p>}
      {result && (
        <>
          <p role="status">
            {result.wouldChange === 0
              ? `Nothing would change. All ${result.linesChecked} lines already match the rules.`
              : `${result.wouldChange} of ${result.linesChecked} lines would change${result.wouldChange > result.changes.length ? ` (showing the first ${result.changes.length})` : ''}.`}
          </p>
          {result.changes.length > 0 && (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Date</th>
                    <th>Line</th>
                    <th className="num">Amount</th>
                    <th>Now</th>
                    <th>Would become</th>
                  </tr>
                </thead>
                <tbody>
                  {result.changes.map((c) => (
                    <tr key={c.transactionId}>
                      <td>{c.postedOn}</td>
                      <td>{c.description}</td>
                      <td className="num">{dollars(c.amountCents)}</td>
                      <td>{summary(c.before)}</td>
                      <td>{summary(c.after)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </details>
  )
}

// ---- the form ---------------------------------------------------------------------------------------------

const CONDITION_KINDS: { key: string; field: string; op: string; label: string; hint: string }[] = [
  { key: 'contains', field: 'description', op: 'contains_any', label: 'Description contains any of', hint: 'words, separated by commas' },
  { key: 'equals', field: 'description', op: 'equals_any', label: 'Description is exactly one of', hint: 'texts, separated by commas' },
  { key: 'starts', field: 'description', op: 'starts_with', label: 'Description starts with', hint: 'some text' },
  { key: 'gte', field: 'amount', op: 'gte', label: 'Amount is at least', hint: 'dollars, like 25.00' },
  { key: 'lte', field: 'amount', op: 'lte', label: 'Amount is at most', hint: 'dollars, like 25.00' },
  { key: 'account', field: 'account', op: 'is', label: 'Account number ends in', hint: 'the last digits' },
  { key: 'kind', field: 'account_kind', op: 'in', label: 'Account kind is any of', hint: 'checking, savings, credit_card, loan, asset, retirement' },
  { key: 'direction', field: 'direction', op: 'is', label: 'Money', hint: 'in or out' },
]

interface Row {
  key: string
  text: string
}

const keyOf = (c: Condition) => CONDITION_KINDS.find((k) => k.field === c.field && k.op === c.op)?.key ?? 'contains'
const textOf = (c: Condition) => (Array.isArray(c.value) ? c.value.join(', ') : String(c.value))

function toCondition(row: Row): Condition | string {
  const kind = CONDITION_KINDS.find((k) => k.key === row.key)!
  const text = row.text.trim()
  if (text === '') return `Fill in “${kind.label}”.`
  const list = text.split(',').map((x) => x.trim()).filter(Boolean)
  switch (kind.key) {
    case 'contains':
    case 'equals':
    case 'kind':
      return { field: kind.field, op: kind.op, value: list }
    case 'gte':
    case 'lte': {
      const n = Number(text.replace(/[$,]/g, ''))
      return Number.isFinite(n) ? { field: kind.field, op: kind.op, value: n } : `“${text}” is not an amount.`
    }
    case 'direction':
      return text === 'in' || text === 'out' ? { field: kind.field, op: kind.op, value: text } : 'Money is either in or out.'
    default:
      return { field: kind.field, op: kind.op, value: text }
  }
}

/**
 * The conditions a new rule starts with when it is made from a line: its words, or, for a check (whose number never repeats), "starts with Check #"
 * with the amount and which way the money went.
 */
function prefilledRows(p: URLSearchParams | null): Row[] {
  const rows: Row[] = [p?.get('starts') ? { key: 'starts', text: p.get('starts')! } : { key: 'contains', text: p?.get('contains') ?? '' }]
  const amount = p?.get('amount')
  if (amount) rows.push({ key: 'gte', text: amount }, { key: 'lte', text: amount })
  const direction = p?.get('direction')
  if (direction === 'in' || direction === 'out') rows.push({ key: 'direction', text: direction })
  return rows
}

function RuleForm({ rule, prefill, categories, onClose }: { rule: RuleRow | null; prefill: URLSearchParams | null; categories: Named[]; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(
    rule?.name ??
      (prefill?.get('contains')
        ? `Lines like ${prefill.get('contains')!.slice(0, 40)}`
        : prefill?.get('starts') && prefill.get('amount')
          ? `Checks for $${prefill.get('amount')}`
          : ''),
  )
  const [active, setActive] = useState(rule?.isActive ?? true)
  const [categoryId, setCategoryId] = useState(rule?.setCategoryId?.toString() ?? prefill?.get('category') ?? '')
  const [type, setType] = useState(rule?.setType ?? prefill?.get('type') ?? '')
  const [payee, setPayee] = useState(rule?.setPayee ?? prefill?.get('payee') ?? '')
  const [tags, setTags] = useState(rule?.tags.join(', ') ?? prefill?.get('tags') ?? '')
  const [rows, setRows] = useState<Row[]>(rule?.conditions.map((c) => ({ key: keyOf(c), text: textOf(c) })) ?? prefilledRows(prefill))
  const [from, setFrom] = useState(() => daysAgoLocal(90))
  const [to, setTo] = useState(() => todayLocal())
  const [result, setResult] = useState<TestResult | null>(null)
  const [applied, setApplied] = useState<number | null>(null)
  const check = useAct()
  const apply = useAct()

  const parsed = rows.map(toCondition)
  const badRow = parsed.find((p): p is string => typeof p === 'string')
  const problem = name.trim() === '' ? 'Give the rule a name.' : categoryId === '' && type === '' ? 'Pick a category or a type for it to set.' : (badRow ?? null)

  const definition = () => ({
    name: name.trim(),
    isActive: active,
    setCategoryId: categoryId ? Number(categoryId) : null,
    setType: type || null,
    setPayee: payee.trim() || null,
    tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
    conditions: parsed.filter((p): p is Condition => typeof p !== 'string'),
  })

  const update = (i: number, patch: Partial<Row>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)))

  return (
    <Panel title={rule ? `Change ${rule.name}` : 'Add a rule'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (!problem) void run(() => (rule ? send('PATCH', `/rules/${rule.id}`, definition()) : send('POST', '/rules', definition())))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>

        <fieldset>
          <legend>When all of these hold</legend>
          {rows.map((r, i) => (
            <div className="split-row" key={i}>
              <select aria-label="Condition" value={r.key} onChange={(e) => update(i, { key: e.target.value })}>
                {CONDITION_KINDS.map((k) => (
                  <option key={k.key} value={k.key}>
                    {k.label}
                  </option>
                ))}
              </select>
              <input aria-label="Value" value={r.text} placeholder={CONDITION_KINDS.find((k) => k.key === r.key)!.hint} onChange={(e) => update(i, { text: e.target.value })} />
              <button type="button" className="btn" aria-label="Remove this condition" onClick={() => setRows(rows.filter((_, j) => j !== i))}>
                ×
              </button>
            </div>
          ))}
          <button type="button" className="btn" onClick={() => setRows([...rows, { key: 'contains', text: '' }])}>
            Add a condition
          </button>
        </fieldset>

        <fieldset>
          <legend>Then set</legend>
          <label>
            Category
            <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">Leave to the category's own type</option>
              {categories.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Type
            <select value={type} onChange={(e) => setType(e.target.value)}>
              <option value="">From the category</option>
              {TYPE_CHOICES.map(([v, l]) => (
                <option key={v} value={v}>
                  {l}
                </option>
              ))}
            </select>
          </label>
          <label>
            Payee
            <input value={payee} onChange={(e) => setPayee(e.target.value)} />
          </label>
          <label>
            Tags (separated by commas)
            <input value={tags} onChange={(e) => setTags(e.target.value)} />
          </label>
        </fieldset>

        <label>
          <span>
            <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> On
          </span>
        </label>

        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || problem !== null}>
            Save
          </button>
          {rule && (
            <button
              type="button"
              className="btn"
              disabled={busy}
              onClick={() => {
                if (window.confirm(`Delete the rule ${rule.name}? Lines it set keep what they were given.`)) void run(() => send('DELETE', `/rules/${rule.id}`))
              }}
            >
              Delete
            </button>
          )}
          {problem && <span className="muted">{problem}</span>}
        </div>
      </form>

      <h3>Try it first</h3>
      <div className="form">
        <label>
          From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>
          To
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <div className="toolbar">
          <button
            type="button"
            className="btn"
            disabled={check.busy || problem !== null}
            onClick={async () => {
              setApplied(null)
              setResult((await check.run(() => send<TestResult>('POST', '/rules/test', { rule: definition(), ruleId: rule?.id, from, to }))) ?? null)
            }}
          >
            See what it would change
          </button>
          {rule && result && (
            <button
              type="button"
              className="btn"
              disabled={apply.busy}
              onClick={async () => {
                if (!window.confirm(`Change ${result.wouldChange} past lines to what this saved rule says? Lines you set by hand are skipped.`)) return
                const done = await apply.run(() => send<{ changed: number }>('POST', `/rules/${rule.id}/apply`, { from, to }))
                if (done) setApplied(done.changed)
              }}
            >
              Apply the saved rule to these lines
            </button>
          )}
        </div>
        <Problem message={check.error ?? apply.error} />
        {applied !== null && <p role="status">Changed {applied} line{applied === 1 ? '' : 's'}. Each change is in the audit log.</p>}
        {result && (
          <>
            <p role="status">
              {result.wouldChange} of {result.linesChecked} lines would change{result.wouldChange > result.changes.length ? ` (showing the first ${result.changes.length})` : ''}.
              {rule === null && result.wouldChange > 0 ? ' Save the rule first to apply it to these lines.' : ''}
            </p>
            {result.changes.length > 0 && (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Date</th>
                      <th>Line</th>
                      <th className="num">Amount</th>
                      <th>Now</th>
                      <th>Would become</th>
                    </tr>
                  </thead>
                  <tbody>
                    {result.changes.map((c) => (
                      <tr key={c.transactionId}>
                        <td>{c.postedOn}</td>
                        <td>{c.description}</td>
                        <td className="num">{dollars(c.amountCents)}</td>
                        <td>{summary(c.before)}</td>
                        <td>{summary(c.after)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </div>
    </Panel>
  )
}

function summary(m: Meaning): string {
  return [m.categoryName ?? 'no category', typeLabel(m.type), m.payee, ...m.tags.map((t) => `#${t}`)].filter(Boolean).join(' · ')
}
