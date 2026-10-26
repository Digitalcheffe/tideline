import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { get, send, type CategoryInfo } from '../api'
import { parseDollars, todayLocal, typeLabel } from '../format'

const TYPES = ['income', 'bill', 'required', 'extra', 'one_time', 'savings']

/** A line you add by hand, such as cash. It is marked as yours and can be deleted again. */
export function ManualEntry({ accountId, accountName, onClose }: { accountId: number; accountName: string; onClose: () => void }) {
  const client = useQueryClient()
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<CategoryInfo[]>('/categories') })

  const [date, setDate] = useState(todayLocal())
  const [direction, setDirection] = useState<'out' | 'in'>('out')
  const [amount, setAmount] = useState('')
  const [description, setDescription] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [type, setType] = useState('extra')
  const [payee, setPayee] = useState('')
  const [notes, setNotes] = useState('')

  const cents = parseDollars(amount)
  const valid = cents !== null && cents > 0 && description.trim() !== '' && date !== ''

  const save = useMutation({
    mutationFn: () =>
      send('POST', '/transactions', {
        accountId,
        postedOn: date,
        amountCents: direction === 'out' ? -cents! : cents!,
        description: description.trim(),
        categoryId: categoryId ? Number(categoryId) : null,
        type,
        payee,
        notes,
      }),
    onSuccess: () => {
      client.invalidateQueries()
      onClose()
    },
  })

  const pickCategory = (value: string) => {
    setCategoryId(value)
    const chosen = categories.data?.find((c) => String(c.id) === value)
    if (chosen) setType(chosen.type)
  }

  return (
    <>
      <div className="scrim" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-label="Add a line">
        <div className="toolbar" style={{ justifyContent: 'space-between' }}>
          <strong>Add a line to {accountName}</strong>
          <button className="btn" onClick={onClose}>
            Close
          </button>
        </div>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (valid) save.mutate()
          }}
        >
          <div className="form">
            <label>
              Date
              <input type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
            </label>
            <label>
              Money
              <select value={direction} onChange={(e) => setDirection(e.target.value as 'in' | 'out')}>
                <option value="out">Went out</option>
                <option value="in">Came in</option>
              </select>
            </label>
            <label>
              Amount
              <input type="text" inputMode="decimal" placeholder="0.00" value={amount} onChange={(e) => setAmount(e.target.value)} required />
            </label>
            <label>
              What was it
              <input type="text" value={description} onChange={(e) => setDescription(e.target.value)} required />
            </label>
            <label>
              Category
              <select value={categoryId} onChange={(e) => pickCategory(e.target.value)}>
                <option value="">No category</option>
                {categories.data?.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Type
              <select value={type} onChange={(e) => setType(e.target.value)}>
                {TYPES.map((x) => (
                  <option key={x} value={x}>
                    {typeLabel(x)}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Payee
              <input type="text" value={payee} onChange={(e) => setPayee(e.target.value)} />
            </label>
            <label>
              Notes
              <textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
            </label>
          </div>
          <div className="toolbar" style={{ marginTop: 8 }}>
            <button className="btn primary" type="submit" disabled={!valid || save.isPending}>
              {save.isPending ? 'Adding…' : 'Add line'}
            </button>
          </div>
          {save.error && <p className="problem" role="alert">{save.error.message}</p>}
        </form>
      </aside>
    </>
  )
}
