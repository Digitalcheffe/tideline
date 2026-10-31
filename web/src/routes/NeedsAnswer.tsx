import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { send, type CategoryInfo, type Line } from '../api'
import { dollars, ruleWords, typeLabel } from '../format'
import { Problem, useAct } from './settings/common'

const EVERYTHING = '1900-01-01'
const FOREVER = '2999-12-31'

export const isCheck = (l: Line) => /^check #/i.test(l.description.trim())

/** Makes the rule for an answered line and tries it on the rest of the pile. Returns the sentence to tell the person. */
async function teach(line: Line, categoryId: number): Promise<string> {
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
    const made = await send<{ id: number }>('POST', '/rules', { name, setCategoryId: categoryId, setPayee: null, tags: [], conditions })
    const applied = await send<{ changed: number }>('POST', `/rules/${made.id}/apply`, { from: EVERYTHING, to: FOREVER })
    return applied.changed > 0
      ? `Answered, and made the rule “${name}”. It sorted ${applied.changed} more ${applied.changed === 1 ? 'line' : 'lines'}.`
      : `Answered, and made the rule “${name}”. No other waiting line matched it.`
  } catch (e) {
    return `Answered. The rule was not made: ${e instanceof Error ? e.message : String(e)}`
  }
}

/**
 * The quick answer on a row of the pile of lines waiting for a category: pick one and press the button. The line leaves the list. An owner can also
 * press the second button to make a rule from the line's words, which sorts the look-alikes at the same time.
 */
export function AnswerCell({ line, categories, isOwner, onDone }: { line: Line; categories: CategoryInfo[]; isOwner: boolean; onDone: (message: string) => void }) {
  const client = useQueryClient()
  const [categoryId, setCategoryId] = useState('')
  const { run, error, busy } = useAct()

  const answer = async (remember: boolean) => {
    if (!categoryId || busy) return
    const message = await run(async () => {
      await send('POST', `/transactions/${line.transactionId}/answer`, { categoryId: Number(categoryId), payee: null })
      return remember ? teach(line, Number(categoryId)) : `Answered “${line.description}”.`
    })
    if (message) {
      onDone(message)
      void client.invalidateQueries()
    }
  }

  return (
    <div className="answer">
      <select aria-label={`Category for ${line.description}`} value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
        <option value="">Pick a category…</option>
        {categories.map((c) => (
          <option key={c.id} value={c.id}>
            {c.name} ({typeLabel(c.type)})
          </option>
        ))}
      </select>
      <button type="button" className="btn primary" disabled={!categoryId || busy} onClick={() => void answer(false)}>
        Answer
      </button>
      {isOwner && (
        <button
          type="button"
          className="btn"
          disabled={!categoryId || busy}
          title={isCheck(line) ? 'Answer, and make a rule for checks of this exact amount' : 'Answer, and make a rule from the words of this line that sorts the look-alikes too'}
          onClick={() => void answer(true)}
        >
          Answer and make a rule
        </button>
      )}
      <Problem message={error} />
    </div>
  )
}
