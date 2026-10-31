import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { dateLabel, dollars, parseDollars } from '../../format'
import { Panel, Problem, centsText, useAct } from './common'

interface Suggestion {
  name: string
  categoryId: number
  categoryName: string
  frequency: 'monthly' | 'yearly'
  everyN: number
  usualCents: number
  minCents: number
  maxCents: number
  dueDay: number
  linksNextOnes: boolean
  lines: { id: number; postedOn: string; description: string; payee: string | null; cents: number; selected: boolean }[]
}

interface Created {
  recurringId: number
  name: string
  linesLinked: number
  linesSkipped: number
  ruleId: number | null
}

/** Where the quick form starts: a category (its bill lines are ticked), or the lines a person picked. */
export interface QuickSeed {
  categoryId?: number
  lineIds?: number[]
}

const HOW: [string, string][] = [
  ['monthly:1', 'Every month'],
  ['monthly:2', 'Every 2 months'],
  ['monthly:3', 'Every 3 months'],
  ['monthly:6', 'Every 6 months'],
  ['yearly:1', 'Once a year'],
]

/**
 * The quick way to track a bill or subscription you can see in the ledger. It opens already filled in from the lines it was started with: the name,
 * the usual amount, the day and how often. Tick the lines that belong to it, press the button, and they are linked (and, if you like, next month's are
 * linked as they arrive). Nothing is made until you press it.
 */
export function QuickRecurring({
  seed, categories, onClose, onDone,
}: {
  seed: QuickSeed
  categories: { id: number; name: string }[]
  onClose: () => void
  onDone: (message: string) => void
}) {
  const [categoryId, setCategoryId] = useState(seed.categoryId ? String(seed.categoryId) : '')
  const lineIds = seed.lineIds?.join(',') ?? ''
  const ready = categoryId !== '' || lineIds !== ''
  const suggestion = useQuery({
    queryKey: ['recurring-suggest', categoryId, lineIds],
    queryFn: () => get<Suggestion>('/recurring/suggest', { categoryId: categoryId || undefined, lineIds: lineIds || undefined }),
    enabled: ready,
  })

  return (
    <Panel title="Track a bill or subscription" onClose={onClose}>
      {lineIds === '' && (
        <label>
          Which category are the payments in?
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">Pick a category</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
      )}
      {ready && suggestion.isLoading && <p className="muted">Looking through the lines…</p>}
      {ready && suggestion.error && <Problem message={suggestion.error.message} />}
      {suggestion.data && ready && !suggestion.isFetching && (
        <QuickForm key={`${suggestion.data.categoryId}:${lineIds}`} s={suggestion.data} onClose={onClose} onDone={onDone} />
      )}
    </Panel>
  )
}

function QuickForm({ s, onClose, onDone }: { s: Suggestion; onClose: () => void; onDone: (message: string) => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(s.name)
  const [usual, setUsual] = useState(centsText(s.usualCents))
  const [min, setMin] = useState(centsText(s.minCents))
  const [max, setMax] = useState(centsText(s.maxCents))
  const [rangeTouched, setRangeTouched] = useState(false)
  const [how, setHow] = useState(`${s.frequency}:${s.everyN}`)
  const [dueDay, setDueDay] = useState(String(s.dueDay))
  const [picked, setPicked] = useState<Set<number>>(new Set(s.lines.filter((l) => l.selected).map((l) => l.id)))
  const [addRule, setAddRule] = useState(s.linksNextOnes)

  const minCents = parseDollars(min)
  const maxCents = parseDollars(max)
  const day = Number(dueDay)
  const problem =
    name.trim() === ''
      ? 'Give it a name.'
      : minCents === null || maxCents === null
        ? 'Enter the lowest and highest you expect as money amounts.'
        : minCents > maxCents
          ? 'The lowest cannot be more than the highest.'
          : !Number.isInteger(day) || day < 1 || day > 31
            ? 'The day of the month must be 1 to 31.'
            : null

  const changeUsual = (text: string) => {
    setUsual(text)
    const cents = parseDollars(text)
    if (!rangeTouched && cents !== null && cents > 0) {
      setMin(centsText(Math.floor(cents * 0.9)))
      setMax(centsText(Math.ceil(cents * 1.1)))
    }
  }

  const toggle = (id: number) => {
    const next = new Set(picked)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setPicked(next)
  }

  const save = async () => {
    const [frequency, everyN] = how.split(':')
    const done = await run(() =>
      send<Created>('POST', '/recurring/quick', {
        name: name.trim(),
        categoryId: s.categoryId,
        minCents,
        maxCents,
        frequency,
        everyN: Number(everyN),
        dueDay: day,
        lineIds: [...picked],
        addRule: addRule && s.linksNextOnes && picked.size > 0,
      }),
    )
    if (done)
      onDone(
        `Added ${done.name} and linked ${done.linesLinked} ${done.linesLinked === 1 ? 'line' : 'lines'}${done.linesSkipped > 0 ? ` (${done.linesSkipped} were already in another item)` : ''}${done.ruleId ? '. Next month\'s will link by themselves.' : '.'}`,
      )
  }

  return (
    <form
      className="form"
      onSubmit={(e) => {
        e.preventDefault()
        if (!problem) void save()
      }}
    >
      <label>
        Name
        <input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </label>
      <p className="muted">
        Category: <strong>{s.categoryName}</strong>
      </p>
      <label>
        Usual amount
        <input value={usual} inputMode="decimal" onChange={(e) => changeUsual(e.target.value)} />
      </label>
      <p className="muted">
        {minCents !== null && maxCents !== null ? `Anything from ${dollars(minCents)} to ${dollars(maxCents)} counts as normal.` : 'Set the range below.'}
      </p>
      <label>
        How often
        <select value={how} onChange={(e) => setHow(e.target.value)}>
          {HOW.map(([v, l]) => (
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </select>
      </label>
      <label>
        Day of the month it usually comes out
        <input type="number" min={1} max={31} value={dueDay} onChange={(e) => setDueDay(e.target.value)} />
      </label>
      <details>
        <summary>Change the range</summary>
        <label>
          Lowest you expect
          <input
            value={min}
            inputMode="decimal"
            onChange={(e) => {
              setMin(e.target.value)
              setRangeTouched(true)
            }}
          />
        </label>
        <label>
          Highest you expect
          <input
            value={max}
            inputMode="decimal"
            onChange={(e) => {
              setMax(e.target.value)
              setRangeTouched(true)
            }}
          />
        </label>
      </details>

      <h3>Lines that belong to it</h3>
      {s.lines.length === 0 ? (
        <p className="muted">No untracked payments found in this category. You can still add it, and link lines later.</p>
      ) : (
        <>
          <div className="toolbar">
            <span className="muted">
              {picked.size} of {s.lines.length} ticked
            </span>
            <button type="button" className="btn" onClick={() => setPicked(new Set(s.lines.map((l) => l.id)))}>
              Tick all
            </button>
            <button type="button" className="btn" onClick={() => setPicked(new Set())}>
              Tick none
            </button>
          </div>
          <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
            <table>
              <thead>
                <tr>
                  <th />
                  <th>Date</th>
                  <th>What it says</th>
                  <th className="num">Amount</th>
                </tr>
              </thead>
              <tbody>
                {s.lines.map((l) => (
                  <tr key={l.id}>
                    <td>
                      <input type="checkbox" aria-label={`Link ${l.description} on ${l.postedOn}`} checked={picked.has(l.id)} onChange={() => toggle(l.id)} />
                    </td>
                    <td>{dateLabel(l.postedOn)}</td>
                    <td>
                      {l.payee ?? l.description}
                      {l.payee && <div className="muted">{l.description}</div>}
                    </td>
                    <td className="num">{dollars(l.cents)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
      {s.linksNextOnes && (
        <label>
          <span>
            <input type="checkbox" checked={addRule} onChange={(e) => setAddRule(e.target.checked)} /> Link next month's lines to it by themselves
          </span>
        </label>
      )}

      <Problem message={error} />
      <div className="toolbar">
        <button className="btn primary" type="submit" disabled={busy || problem !== null}>
          {busy ? 'Adding…' : picked.size > 0 ? `Add it and link ${picked.size} ${picked.size === 1 ? 'line' : 'lines'}` : 'Add it'}
        </button>
        {problem && <span className="muted">{problem}</span>}
      </div>
    </form>
  )
}
