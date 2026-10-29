import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { get, getLines, send, type CategoryInfo, type Line } from '../api'
import { useMe } from '../auth'
import { Badge, Card, Empty, ErrorBox, Loading, Money, drillPath } from '../components'
import { dateLabel, dollars, ruleWords, typeLabel } from '../format'
import { useNeedsCategory } from '../needs'
import { Problem, useAct } from './settings/common'

const EVERYTHING = '1900-01-01'
const FOREVER = '2999-12-31'
const PAGE = 25

const isCheck = (l: Line) => /^check #/i.test(l.description.trim())

/** Makes the rule for an answered line and tries it on the rest of the pile. Returns the sentence to tell the person. */
async function teach(line: Line, categoryId: number, payee: string): Promise<string> {
  const abs = Math.abs(line.amountCents) / 100
  // a check's number never repeats, so a check is taught by its amount; anything else by its words
  const words = ruleWords(line.description)
  const conditions = isCheck(line)
    ? [
        { field: 'description', op: 'starts_with', value: 'Check #' },
        { field: 'amount', op: 'gte', value: abs },
        { field: 'amount', op: 'lte', value: abs },
        { field: 'direction', op: 'is', value: line.amountCents < 0 ? 'out' : 'in' },
      ]
    : [{ field: 'description', op: 'contains_any', value: [words] }]
  const name = isCheck(line) ? `Checks for ${dollars(Math.abs(line.amountCents))}` : `Lines like ${words.slice(0, 40)}`
  try {
    const made = await send<{ id: number }>('POST', '/rules', { name, setCategoryId: categoryId, setPayee: payee || null, tags: [], conditions })
    const applied = await send<{ changed: number }>('POST', `/rules/${made.id}/apply`, { from: EVERYTHING, to: FOREVER })
    return applied.changed > 0
      ? `Answered, and made the rule “${name}”. It sorted ${applied.changed} more ${applied.changed === 1 ? 'line' : 'lines'}.`
      : `Answered, and made the rule “${name}”. No other waiting line matched it.`
  } catch (e) {
    return `Answered. The rule was not made: ${e instanceof Error ? e.message : String(e)}`
  }
}

function Row({ line, categories, canAnswer, isOwner, onDone }: { line: Line; categories: CategoryInfo[]; canAnswer: boolean; isOwner: boolean; onDone: (message: string) => void }) {
  const [categoryId, setCategoryId] = useState('')
  const [payee, setPayee] = useState('')
  const [remember, setRemember] = useState(false)
  const { run, error, busy } = useAct()

  const submit = async () => {
    if (!categoryId || busy) return
    const message = await run(async () => {
      await send('POST', `/transactions/${line.transactionId}/answer`, { categoryId: Number(categoryId), payee: payee.trim() || null })
      return remember ? teach(line, Number(categoryId), payee.trim()) : 'Answered.'
    })
    if (message) onDone(message)
  }

  return (
    <li className="needs-row">
      <div className="needs-line">
        <span className="muted">
          {dateLabel(line.postedOn)} · {line.accountName}
        </span>
        <strong>{line.description}</strong>
        {isCheck(line) && <Badge tone="info">Check</Badge>}
        {line.payee && line.payee !== line.description && <span className="muted">{line.payee}</span>}
        <Money cents={line.amountCents} className="needs-amount" />
      </div>
      {line.isSplitPart ? (
        <Link to={`/lines?tx=${line.transactionId}`}>Open it to change this part of the split</Link>
      ) : canAnswer ? (
        <div className="toolbar">
          <select aria-label={`Category for ${line.description}`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">Pick a category…</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name} ({typeLabel(c.type)})
              </option>
            ))}
          </select>
          <input
            aria-label={`Payee for ${line.description}`}
            placeholder="Who, if you know"
            value={payee}
            maxLength={100}
            onChange={(e) => setPayee(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && void submit()}
          />
          {isOwner && (
            <label title={isCheck(line) ? 'Make a rule from checks of this exact amount' : 'Make a rule from the words of this line and use it on the others waiting'}>
              <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> Remember as a rule
            </label>
          )}
          <button type="button" className="btn primary" disabled={!categoryId || busy} onClick={() => void submit()}>
            Answer
          </button>
        </div>
      ) : null}
      <Problem message={error} />
    </li>
  )
}

/** The pile of lines the rules could not place, one at a time: pick what it was, and it is gone from the list. */
export function Needs() {
  const me = useMe()
  const canAnswer = me.user.role !== 'viewer'
  const isOwner = me.user.role === 'owner'
  const needs = useNeedsCategory()
  const list = useQuery({ queryKey: ['needs-list'], queryFn: () => getLines(`from=${EVERYTHING}&to=${FOREVER}&needs=true&sort=date_asc&pageSize=${PAGE}`) })
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<CategoryInfo[]>('/categories') })
  const [message, setMessage] = useState<string | null>(null)

  if (list.isLoading || categories.isLoading) return <Loading what="the lines waiting" />
  if (list.error || !list.data || !categories.data) return <ErrorBox error={list.error ?? categories.error} />

  const waiting = needs.data?.count ?? list.data.total
  const real = categories.data.filter((c) => c.name !== 'Uncategorized')
  const pileLink = drillPath(needs.data?.drill)

  return (
    <>
      <h1>Needs a category</h1>
      <Card
        title={waiting === 0 ? 'Nothing is waiting' : `${waiting} ${waiting === 1 ? 'line is' : 'lines are'} waiting`}
        aside={pileLink && waiting > 0 && <Link to={pileLink}>See them in the Ledger</Link>}
      >
        <p className="muted">
          These are lines the rules could not place, and checks, which the bank only gives as a number. Pick what each one was and press Answer; it leaves the list.
          {isOwner && ' Tick Remember as a rule and the next lines like it sort themselves.'}
        </p>
        {!canAnswer && <p className="muted">You can look, but only a member or an owner can answer.</p>}
        {message && <p role="status">{message}</p>}
        {list.data.items.length === 0 ? (
          <Empty>Every line has a category.</Empty>
        ) : (
          <>
            <ul className="needs-list">
              {list.data.items.map((l) => (
                <Row key={`${l.transactionId}-${l.splitLineId ?? 'r'}`} line={l} categories={real} canAnswer={canAnswer} isOwner={isOwner} onDone={setMessage} />
              ))}
            </ul>
            {list.data.total > list.data.items.length && (
              <p className="muted">
                Showing the oldest {list.data.items.length} of {list.data.total}. Answer these and the next ones appear.
              </p>
            )}
          </>
        )}
      </Card>
    </>
  )
}
