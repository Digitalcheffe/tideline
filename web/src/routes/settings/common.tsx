import { useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'

/** Runs one change, refreshes everything on screen, and keeps the plain-words reason when it is refused. */
export function useAct(after?: () => void) {
  const client = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function run<T>(work: () => Promise<T>): Promise<T | undefined> {
    setBusy(true)
    setError(null)
    try {
      const result = await work()
      await client.invalidateQueries()
      after?.()
      return result
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      return undefined
    } finally {
      setBusy(false)
    }
  }
  return { run, error, busy, clear: () => setError(null) }
}

export function Problem({ message }: { message: string | null }) {
  return message ? (
    <div className="problem" role="alert">
      {message}
    </div>
  ) : null
}

/** A small panel that opens above a list to add or change one thing. */
export function Panel({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <section className="panel" aria-label={title}>
      <div className="toolbar" style={{ justifyContent: 'space-between' }}>
        <strong>{title}</strong>
        <button type="button" className="btn" onClick={onClose}>
          Cancel
        </button>
      </div>
      {children}
    </section>
  )
}

/** Cents as the text of a money field: 12345 becomes "123.45". */
export function centsText(cents: number | null | undefined): string {
  return cents === null || cents === undefined ? '' : (cents / 100).toFixed(2)
}

export function when(iso: string | null | undefined): string {
  if (!iso) return 'never'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export const TYPE_CHOICES: [string, string][] = [
  ['income', 'Income'],
  ['bill', 'Bill'],
  ['required', 'Required'],
  ['extra', 'Extra'],
  ['one_time', 'One-time'],
  ['savings', 'Savings'],
]
