import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { dollars, parseDollars, todayLocal, typeLabel } from '../../format'
import { FindBills } from './FindBills'
import { Panel, Problem, centsText, useAct } from './common'

interface RecurringRow {
  id: number
  name: string
  categoryId: number
  categoryName: string
  categoryType: string
  expectedMinCents: number
  expectedMaxCents: number
  frequency: string
  everyN: number
  dueDay: number
  firstDueOn: string
  graceDays: number
  accountId: number | null
  isActive: boolean
  endedOn: string | null
  lineCount: number
}

interface Named {
  id: number
  name: string
}

export function RecurringSettings() {
  const recurring = useQuery({ queryKey: ['settings', 'recurring'], queryFn: () => get<RecurringRow[]>('/recurring') })
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<Named[]>('/categories') })
  const accounts = useQuery({ queryKey: ['accounts-list'], queryFn: () => get<Named[]>('/accounts') })
  const [editing, setEditing] = useState<RecurringRow | 'new' | null>(null)
  const { run, error, busy } = useAct()

  if (recurring.isLoading || categories.isLoading || accounts.isLoading) return <Loading what="recurring items" />
  if (recurring.error || !recurring.data || !categories.data || !accounts.data) return <ErrorBox error={recurring.error ?? categories.error ?? accounts.error} />

  return (
    <>
      {editing && (
        <RecurringForm
          key={editing === 'new' ? 'new' : editing.id}
          item={editing === 'new' ? null : editing}
          categories={categories.data}
          accounts={accounts.data}
          onClose={() => setEditing(null)}
        />
      )}
      <FindBills />
      <Card
        title="Recurring items"
        aside={
          <button type="button" className="btn primary" onClick={() => setEditing('new')}>
            Add one
          </button>
        }
      >
        <p className="muted">Bills and subscriptions you expect on a schedule. The Bills screen shows each month as paid, due or missed against these.</p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Category</th>
                <th>Expected</th>
                <th>When</th>
                <th className="num">Lines</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {recurring.data.map((r) => (
                <tr key={r.id}>
                  <td>
                    {r.name} {!r.isActive && <Badge tone="muted">{r.endedOn ? `ended ${r.endedOn}` : 'paused'}</Badge>}
                  </td>
                  <td>
                    {r.categoryName} <span className="muted">({typeLabel(r.categoryType)})</span>
                  </td>
                  <td>
                    {r.expectedMinCents === r.expectedMaxCents ? dollars(r.expectedMinCents) : `${dollars(r.expectedMinCents)} to ${dollars(r.expectedMaxCents)}`}
                  </td>
                  <td>
                    {r.frequency === 'yearly' ? 'Yearly' : r.everyN > 1 ? `Every ${r.everyN} months` : 'Monthly'}, day {r.dueDay}
                  </td>
                  <td className="num">{r.lineCount}</td>
                  <td>
                    <button type="button" className="btn" onClick={() => setEditing(r)}>
                      Change
                    </button>{' '}
                    <button
                      type="button"
                      className="btn"
                      disabled={busy}
                      title="Removes the item. Its lines stay in the ledger. To keep it for later, use Change and untick Still expected."
                      onClick={() => {
                        if (window.confirm(`Delete ${r.name}? Its ${r.lineCount} line${r.lineCount === 1 ? '' : 's'} stay in the ledger, no longer linked to it.\n\nTo keep it for later, use Change and untick Still expected instead.`))
                          void run(() => send('DELETE', `/recurring/${r.id}`))
                      }}
                    >
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <Problem message={error} />
      </Card>
    </>
  )
}

function RecurringForm({ item, categories, accounts, onClose }: { item: RecurringRow | null; categories: Named[]; accounts: Named[]; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(item?.name ?? '')
  const [categoryId, setCategoryId] = useState(item?.categoryId.toString() ?? '')
  const [min, setMin] = useState(centsText(item?.expectedMinCents))
  const [max, setMax] = useState(centsText(item?.expectedMaxCents))
  const [frequency, setFrequency] = useState(item?.frequency ?? 'monthly')
  const [everyN, setEveryN] = useState(String(item?.everyN ?? 1))
  const [dueDay, setDueDay] = useState(String(item?.dueDay ?? 1))
  const [firstDueOn, setFirstDueOn] = useState(item?.firstDueOn ?? todayLocal())
  const [grace, setGrace] = useState(String(item?.graceDays ?? 5))
  const [accountId, setAccountId] = useState(item?.accountId?.toString() ?? '')
  const [active, setActive] = useState(item?.isActive ?? true)
  const [endedOn, setEndedOn] = useState(item?.endedOn ?? '')

  const minCents = parseDollars(min)
  const maxCents = parseDollars(max)
  const problem =
    name.trim() === ''
      ? 'Give it a name.'
      : categoryId === ''
        ? 'Pick a category.'
        : minCents === null || maxCents === null
          ? 'Enter the lowest and highest you expect as money amounts.'
          : minCents > maxCents
            ? 'The lowest cannot be more than the highest.'
            : null

  const body = () => ({
    name: name.trim(),
    categoryId: Number(categoryId),
    expectedMinCents: minCents,
    expectedMaxCents: maxCents,
    frequency,
    everyN: Number(everyN) || 1,
    dueDay: Number(dueDay),
    firstDueOn,
    graceDays: Number(grace) || 0,
    accountId: accountId ? Number(accountId) : null,
    isActive: active,
    endedOn: endedOn || (item ? '' : null),
  })

  return (
    <Panel title={item ? `Change ${item.name}` : 'Add a recurring item'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (!problem) void run(() => (item ? send('PATCH', `/recurring/${item.id}`, body()) : send('POST', '/recurring', body())))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Category
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">Pick a category</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Lowest you expect
          <input value={min} inputMode="decimal" onChange={(e) => setMin(e.target.value)} />
        </label>
        <label>
          Highest you expect
          <input value={max} inputMode="decimal" onChange={(e) => setMax(e.target.value)} />
        </label>
        <label>
          How often
          <select value={frequency} onChange={(e) => setFrequency(e.target.value)}>
            <option value="monthly">Monthly</option>
            <option value="yearly">Yearly</option>
          </select>
        </label>
        {frequency === 'monthly' && (
          <label>
            Every how many months
            <input type="number" min={1} max={12} value={everyN} onChange={(e) => setEveryN(e.target.value)} />
          </label>
        )}
        <label>
          Due day of the month
          <input type="number" min={1} max={31} value={dueDay} onChange={(e) => setDueDay(e.target.value)} />
        </label>
        <label>
          First due on
          <input type="date" value={firstDueOn} onChange={(e) => setFirstDueOn(e.target.value)} />
        </label>
        <label>
          Days of grace before it counts as missed
          <input type="number" min={0} max={30} value={grace} onChange={(e) => setGrace(e.target.value)} />
        </label>
        <label>
          Paid from (optional)
          <select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            <option value="">Any account</option>
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        </label>
        {item && (
          <>
            <label>
              <span>
                <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Still expected
              </span>
            </label>
            <label>
              Ended on
              <input type="date" value={endedOn} onChange={(e) => setEndedOn(e.target.value)} />
            </label>
          </>
        )}
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || problem !== null}>
            Save
          </button>
          {problem && <span className="muted">{problem}</span>}
        </div>
      </form>
    </Panel>
  )
}
