import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { getLines, get, type AccountInfo, type CategoryInfo } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money } from '../components'
import { dateLabel, dollars, typeLabel } from '../format'
import { TransactionDrawer } from './TransactionDrawer'

const SORTS: { key: string; label: string }[] = [
  { key: 'date_desc', label: 'Newest first' },
  { key: 'date_asc', label: 'Oldest first' },
  { key: 'amount_desc', label: 'Largest in first' },
  { key: 'amount_asc', label: 'Largest out first' },
]

/** The drill-down view: every number on every screen opens here (docs/08-screens.md, "Lines"). */
export function Lines() {
  const [params, setParams] = useSearchParams()
  const open = params.get('tx')
  const apiParams = new URLSearchParams(params)
  apiParams.delete('tx')

  const hasRange = apiParams.has('from') && apiParams.has('to')
  const page = Number(apiParams.get('page') ?? '1') || 1

  const lines = useQuery({
    queryKey: ['lines', apiParams.toString()],
    queryFn: () => getLines(apiParams.toString()),
    enabled: hasRange,
  })
  const accounts = useQuery({ queryKey: ['accounts'], queryFn: () => get<AccountInfo[]>('/accounts') })
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<CategoryInfo[]>('/categories') })

  const set = (key: string, value: string | null) => {
    const next = new URLSearchParams(params)
    if (value === null || value === '') next.delete(key)
    else next.set(key, value)
    if (key !== 'page') next.delete('page')
    setParams(next)
  }

  if (!hasRange)
    return (
      <>
        <h1>Lines</h1>
        <Empty>
          Open this page from a number on another screen, or pick a period:{' '}
          <button className="btn" onClick={() => { const n = new Date(); const y = n.getFullYear(); const m = String(n.getMonth() + 1).padStart(2, '0'); const last = new Date(y, n.getMonth() + 1, 0).getDate(); setParams({ from: `${y}-${m}-01`, to: `${y}-${m}-${last}` }) }}>
            This month
          </button>
        </Empty>
      </>
    )

  const chips: string[] = []
  const accountName = (id: string) => accounts.data?.find((a) => String(a.id) === id)?.name ?? `account ${id}`
  const categoryName = (id: string) => (id === '0' ? 'No category' : (categories.data?.find((c) => String(c.id) === id)?.name ?? `category ${id}`))
  chips.push(`${dateLabel(apiParams.get('from')!)} to ${dateLabel(apiParams.get('to')!)}`)
  if (apiParams.get('type')) chips.push(apiParams.get('type')!.split(',').map(typeLabel).join(' or '))
  if (apiParams.get('category')) chips.push(apiParams.get('category')!.split(',').map(categoryName).join(', '))
  if (apiParams.get('account')) chips.push(apiParams.get('account')!.split(',').map(accountName).join(', '))
  if (apiParams.get('tag')) chips.push(`tagged ${apiParams.get('tag')}`)
  if (apiParams.get('review') === 'true') chips.push('to review')
  if (apiParams.get('recurring')) chips.push(apiParams.get('recurring') === 'none' ? 'not a recurring item' : 'one recurring item')
  if (apiParams.get('move') === 'include') chips.push('with transfers')
  if (apiParams.get('move') === 'only') chips.push('transfers only')
  if (apiParams.get('direction')) chips.push(apiParams.get('direction') === 'in' ? 'money in' : 'money out')
  if (apiParams.get('q')) chips.push(`“${apiParams.get('q')}”`)
  if (apiParams.get('minCents')) chips.push(`at least ${dollars(Number(apiParams.get('minCents')))}`)
  if (apiParams.get('maxCents')) chips.push(`at most ${dollars(Number(apiParams.get('maxCents')))}`)

  return (
    <>
      <h1>Lines</h1>
      <div className="chips" aria-label="Filters">
        {chips.map((c) => (
          <span className="chip" key={c}>
            {c}
          </span>
        ))}
      </div>

      <div className="toolbar">
        <input
          type="search"
          placeholder="Search description, payee, notes"
          aria-label="Search"
          defaultValue={apiParams.get('q') ?? ''}
          onKeyDown={(e) => e.key === 'Enter' && set('q', (e.target as HTMLInputElement).value)}
        />
        <select aria-label="Sort" value={apiParams.get('sort') ?? 'date_desc'} onChange={(e) => set('sort', e.target.value)}>
          {SORTS.map((s) => (
            <option key={s.key} value={s.key}>
              {s.label}
            </option>
          ))}
        </select>
        <label>
          <input type="checkbox" checked={apiParams.get('move') === 'include'} onChange={(e) => set('move', e.target.checked ? 'include' : null)} /> Show transfers
        </label>
      </div>

      <Card
        title={lines.data ? `${lines.data.summary.count} ${lines.data.summary.count === 1 ? 'line' : 'lines'}` : 'Lines'}
        aside={lines.data && <>Total <Money cents={lines.data.summary.sumCents} /></>}
      >
        {lines.isLoading && <Loading what="lines" />}
        {lines.error && <ErrorBox error={lines.error} />}
        {lines.data && lines.data.items.length === 0 && <Empty>No lines match.</Empty>}
        {lines.data && lines.data.items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Account</th>
                  <th>Description</th>
                  <th>Meaning</th>
                  <th className="num">Amount</th>
                </tr>
              </thead>
              <tbody>
                {lines.data.items.map((l) => (
                  <tr
                    key={`${l.transactionId}-${l.splitLineId ?? 'r'}`}
                    className="clickable"
                    tabIndex={0}
                    onClick={() => set('tx', String(l.transactionId))}
                    onKeyDown={(e) => e.key === 'Enter' && set('tx', String(l.transactionId))}
                  >
                    <td>{dateLabel(l.postedOn)}</td>
                    <td>{l.accountName}</td>
                    <td>
                      {l.description}
                      {l.payee && l.payee !== l.description && <span className="cell-note">{l.payee}</span>}
                      {l.isSplitPart && <Badge tone="info">Split part</Badge>}
                    </td>
                    <td>
                      {l.type ? (
                        <>
                          {typeLabel(l.type)}
                          <span className="cell-note">{l.categoryName ?? 'No category'}</span>
                        </>
                      ) : (
                        <Badge tone="muted">Transfer</Badge>
                      )}
                      {l.tags.map((t) => (
                        <Badge key={t} tone={t === 'review' ? 'warn' : 'muted'}>
                          {t}
                        </Badge>
                      ))}
                    </td>
                    <td className="num">
                      <Money cents={l.amountCents} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {lines.data && lines.data.total > lines.data.pageSize && (
          <div className="pager">
            <button className="btn" disabled={page <= 1} onClick={() => set('page', String(page - 1))}>
              Previous
            </button>
            <span className="muted">
              Page {lines.data.page} of {Math.ceil(lines.data.total / lines.data.pageSize)}
            </span>
            <button className="btn" disabled={page * lines.data.pageSize >= lines.data.total} onClick={() => set('page', String(page + 1))}>
              Next
            </button>
          </div>
        )}
      </Card>

      {open && <TransactionDrawer id={Number(open)} onClose={() => set('tx', null)} />}
    </>
  )
}
