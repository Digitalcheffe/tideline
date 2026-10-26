import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { getLines, get, send, type AccountInfo, type CategoryInfo } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money } from '../components'
import { dateLabel, dollars, typeLabel } from '../format'
import { usePeriod } from '../period'
import { TransactionDrawer } from './TransactionDrawer'

const SORTS: { key: string; label: string }[] = [
  { key: 'date_desc', label: 'Newest first' },
  { key: 'date_asc', label: 'Oldest first' },
  { key: 'amount_desc', label: 'Largest in first' },
  { key: 'amount_asc', label: 'Largest out first' },
]

/**
 * The ledger: every line, for the period in the menu. It is also the drill-down view that every number on every screen
 * opens (docs/08-screens.md, "Lines"), so the filters live in the URL.
 */
export function Lines() {
  const [params, setParams] = useSearchParams()
  const period = usePeriod()
  const open = params.get('tx')
  const apiParams = new URLSearchParams(params)
  apiParams.delete('tx')
  // no period in the address means the one in the menu (this month)
  if (!apiParams.has('from')) apiParams.set('from', period.from)
  if (!apiParams.has('to')) apiParams.set('to', period.to)
  const page = Number(apiParams.get('page') ?? '1') || 1

  const lines = useQuery({
    queryKey: ['lines', apiParams.toString()],
    queryFn: () => getLines(apiParams.toString()),
  })
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const queryKey = apiParams.toString()
  useEffect(() => setSelected(new Set()), [queryKey])
  const toggle = (id: number) =>
    setSelected((prev) => {
      const n = new Set(prev)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
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

  const filterKeys = ['type', 'category', 'account', 'asset', 'tag', 'review', 'recurring', 'direction', 'q', 'minCents', 'maxCents', 'move', 'leftOut']
  const leftOutView = apiParams.get('leftOut') === 'true'
  const filtersActive = filterKeys.some((k) => apiParams.has(k))
  const clearFilters = () => {
    const next = new URLSearchParams(params)
    filterKeys.forEach((k) => next.delete(k))
    next.delete('page')
    setParams(next)
  }

  const chips: string[] = []
  const accountName = (id: string) => accounts.data?.find((a) => String(a.id) === id)?.name ?? `account ${id}`
  const categoryName = (id: string) => (id === '0' ? 'No category' : (categories.data?.find((c) => String(c.id) === id)?.name ?? `category ${id}`))
  chips.push(`${dateLabel(apiParams.get('from')!)} to ${dateLabel(apiParams.get('to')!)}`)
  if (apiParams.get('type')) chips.push(apiParams.get('type')!.split(',').map(typeLabel).join(' or '))
  if (apiParams.get('category')) chips.push(apiParams.get('category')!.split(',').map(categoryName).join(', '))
  if (apiParams.get('account')) chips.push(apiParams.get('account')!.split(',').map(accountName).join(', '))
  if (apiParams.get('asset')) chips.push(`spent on ${accountName(apiParams.get('asset')!)}`)
  if (apiParams.get('tag')) chips.push(`tagged ${apiParams.get('tag')}`)
  if (apiParams.get('review') === 'true') chips.push('to review')
  if (apiParams.get('recurring')) chips.push(apiParams.get('recurring') === 'none' ? 'not a recurring item' : apiParams.get('recurring') === 'other' ? 'not in a bill row' : 'one recurring item')
  if (apiParams.get('move') === 'include') chips.push('with transfers')
  if (apiParams.get('move') === 'only') chips.push('transfers only')
  if (leftOutView) chips.push('left out of the totals')
  if (apiParams.get('direction')) chips.push(apiParams.get('direction') === 'in' ? 'money in' : 'money out')
  if (apiParams.get('q')) chips.push(`“${apiParams.get('q')}”`)
  if (apiParams.get('minCents')) chips.push(`at least ${dollars(Number(apiParams.get('minCents')))}`)
  if (apiParams.get('maxCents')) chips.push(`at most ${dollars(Number(apiParams.get('maxCents')))}`)

  return (
    <>
      <h1>Ledger</h1>
      <div className="chips" aria-label="Filters">
        {chips.map((c) => (
          <span className="chip" key={c}>
            {c}
          </span>
        ))}
      </div>

      <div className="toolbar">
        <select aria-label="Account" value={apiParams.get('account') ?? ''} onChange={(e) => set('account', e.target.value)}>
          <option value="">All accounts</option>
          {accounts.data?.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </select>
        <select aria-label="Category" value={apiParams.get('category') ?? ''} onChange={(e) => set('category', e.target.value)}>
          <option value="">All categories</option>
          {categories.data?.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
        <select aria-label="Type" value={apiParams.get('type') ?? ''} onChange={(e) => set('type', e.target.value)}>
          <option value="">All types</option>
          {['income', 'bill', 'required', 'extra', 'one_time', 'savings'].map((t) => (
            <option key={t} value={t}>
              {typeLabel(t)}
            </option>
          ))}
        </select>
        <select aria-label="Direction" value={apiParams.get('direction') ?? ''} onChange={(e) => set('direction', e.target.value)}>
          <option value="">In and out</option>
          <option value="in">Money in</option>
          <option value="out">Money out</option>
        </select>
        <label>
          <input type="checkbox" checked={apiParams.get('review') === 'true'} onChange={(e) => set('review', e.target.checked ? 'true' : null)} /> To review
        </label>
        {filtersActive && (
          <button className="btn" type="button" onClick={clearFilters}>
            Clear filters
          </button>
        )}
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
          <input type="checkbox" checked={apiParams.get('move') === 'include'} disabled={leftOutView} onChange={(e) => set('move', e.target.checked ? 'include' : null)} /> Show transfers
        </label>
        <label title="Lines you took out of every total, such as a duplicate. Open one to bring it back.">
          <input type="checkbox" checked={leftOutView} onChange={(e) => set('leftOut', e.target.checked ? 'true' : null)} /> Left out
        </label>
      </div>

      <Card
        title={lines.data ? `${lines.data.summary.count} ${lines.data.summary.count === 1 ? 'line' : 'lines'}` : 'Lines'}
        aside={lines.data && <>Total <Money cents={lines.data.summary.sumCents} /></>}
      >
        {lines.isLoading && <Loading what="lines" />}
        {lines.error && <ErrorBox error={lines.error} />}
        {lines.data && lines.data.items.length === 0 && <Empty>{leftOutView ? 'Nothing is left out.' : 'No lines match.'}</Empty>}
        {selected.size > 0 && categories.data && (
          <BulkBar ids={[...selected]} categories={categories.data} leftOut={leftOutView} onDone={() => setSelected(new Set())} />
        )}
        {lines.data && lines.data.items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th style={{ width: '1.5rem' }}>
                    <input
                      type="checkbox"
                      aria-label="Select all lines on this page"
                      checked={lines.data.items.filter((l) => l.type).every((l) => selected.has(l.transactionId))}
                      onChange={(e) => setSelected(e.target.checked ? new Set(lines.data!.items.filter((l) => l.type).map((l) => l.transactionId)) : new Set())}
                    />
                  </th>
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
                    className={`clickable ${selected.has(l.transactionId) ? 'selected' : ''} ${l.isLeftOut ? 'left-out' : ''}`}
                    tabIndex={0}
                    onClick={() => set('tx', String(l.transactionId))}
                    onKeyDown={(e) => e.key === 'Enter' && set('tx', String(l.transactionId))}
                  >
                    <td onClick={(e) => e.stopPropagation()}>
                      {l.type && <input type="checkbox" aria-label={`Select ${l.description}`} checked={selected.has(l.transactionId)} onChange={() => toggle(l.transactionId)} />}
                    </td>
                    <td>{dateLabel(l.postedOn)}</td>
                    <td>
                      <Pick onPick={() => set('account', String(l.accountId))} title={`Only ${l.accountName}`}>
                        {l.accountName}
                      </Pick>
                    </td>
                    <td>
                      {l.description}
                      {l.payee && l.payee !== l.description && (
                        <span className="cell-note">
                          <Pick onPick={() => set('q', l.payee)} title={`Search for ${l.payee}`}>
                            {l.payee}
                          </Pick>
                        </span>
                      )}
                      {l.isSplitPart && <Badge tone="info">Split part</Badge>}
                    </td>
                    <td>
                      {l.type ? (
                        <>
                          <Pick onPick={() => set('type', l.type)} title="Only this type">
                            {typeLabel(l.type)}
                          </Pick>
                          <span className="cell-note">
                            <Pick onPick={() => set('category', String(l.categoryId ?? 0))} title="Only this category">
                              {l.categoryName ?? 'No category'}
                            </Pick>
                          </span>
                        </>
                      ) : (
                        <Badge tone="muted">Transfer</Badge>
                      )}
                      {l.tags.map((t) => (
                        <Pick key={t} onPick={() => set('tag', t)} title={`Only lines tagged ${t}`}>
                          <Badge tone={t === 'review' ? 'warn' : 'muted'}>{t}</Badge>
                        </Pick>
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

/** Click a value in a row to show only lines like it. */
function Pick({ onPick, title, children }: { onPick: () => void; title: string; children: React.ReactNode }) {
  return (
    <button
      type="button"
      className="pick"
      title={title}
      onClick={(e) => {
        e.stopPropagation()
        onPick()
      }}
    >
      {children}
    </button>
  )
}

/** Change several lines at once: the quick way to fix a pile of tags or categories. */
function BulkBar({ ids, categories, leftOut, onDone }: { ids: number[]; categories: CategoryInfo[]; leftOut: boolean; onDone: () => void }) {
  const client = useQueryClient()
  const [categoryId, setCategoryId] = useState('')
  const [type, setType] = useState('')
  const [addTags, setAddTags] = useState('')
  const [removeTags, setRemoveTags] = useState('')
  const [assetId, setAssetId] = useState('')
  const assetChoices = useQuery({ queryKey: ['accounts'], queryFn: () => get<AccountInfo[]>('/accounts') }).data?.filter((a) => a.kind === 'asset') ?? []

  const words = (text: string) => text.split(',').map((x) => x.trim()).filter(Boolean)
  const nothing = !categoryId && !type && !assetId && words(addTags).length === 0 && words(removeTags).length === 0

  const apply = useMutation({
    mutationFn: () =>
      send<{ updated: number; skippedTransfers: number; notFound: number }>('POST', '/transactions/bulk', {
        ids,
        set: {
          categoryId: categoryId ? Number(categoryId) : undefined,
          type: type || undefined,
          assetId: assetId ? Number(assetId) : undefined,
          addTags: words(addTags),
          removeTags: words(removeTags),
        },
      }),
    onSuccess: () => {
      client.invalidateQueries()
      onDone()
    },
  })

  const pair = useMutation({
    mutationFn: () => send<{ id: number }>('POST', '/moves', { transactionIds: ids }),
    onSuccess: () => {
      client.invalidateQueries()
      onDone()
    },
  })

  const pickCategory = (value: string) => {
    setCategoryId(value)
    const chosen = categories.find((c) => String(c.id) === value)
    if (chosen && !type) setType(chosen.type)
  }

  const [reason, setReason] = useState('')
  const out = useMutation({
    mutationFn: () => send<{ leftOut: number; skipped: number }>('POST', '/transactions/leave-out', { ids, reason: reason.trim() || undefined }),
    onSuccess: () => {
      client.invalidateQueries()
      onDone()
    },
  })
  const back = useMutation({
    mutationFn: () => send('POST', '/transactions/bring-back', { ids }),
    onSuccess: () => {
      client.invalidateQueries()
      onDone()
    },
  })

  if (leftOut)
    return (
      <div className="bulkbar">
        <strong>{ids.length} selected</strong>
        <button className="btn primary" type="button" disabled={back.isPending} onClick={() => back.mutate()}>
          {back.isPending ? 'Working…' : 'Bring back into the totals'}
        </button>
        <button className="btn" type="button" onClick={onDone}>
          Clear selection
        </button>
        {back.error && (
          <span className="problem" role="alert">
            {back.error.message}
          </span>
        )}
      </div>
    )

  return (
    <form
      className="bulkbar"
      onSubmit={(e) => {
        e.preventDefault()
        apply.mutate()
      }}
    >
      <strong>{ids.length} selected</strong>
      <select aria-label="Set category" value={categoryId} onChange={(e) => pickCategory(e.target.value)}>
        <option value="">Category: no change</option>
        {categories.map((c) => (
          <option key={c.id} value={c.id}>
            {c.name}
          </option>
        ))}
      </select>
      <select aria-label="Set type" value={type} onChange={(e) => setType(e.target.value)}>
        <option value="">Type: no change</option>
        {['income', 'bill', 'required', 'extra', 'one_time', 'savings'].map((t) => (
          <option key={t} value={t}>
            {typeLabel(t)}
          </option>
        ))}
      </select>
      {assetChoices.length > 0 && (
        <select aria-label="Set asset" value={assetId} onChange={(e) => setAssetId(e.target.value)}>
          <option value="">Asset: no change</option>
          <option value="0">Asset: none</option>
          {assetChoices.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </select>
      )}
      <input type="text" placeholder="Add tags" aria-label="Add tags" value={addTags} onChange={(e) => setAddTags(e.target.value)} />
      <input type="text" placeholder="Remove tags" aria-label="Remove tags" value={removeTags} onChange={(e) => setRemoveTags(e.target.value)} />
      <button className="btn primary" type="submit" disabled={nothing || apply.isPending}>
        {apply.isPending ? 'Saving…' : 'Apply'}
      </button>
      {ids.length === 2 && (
        <button
          className="btn"
          type="button"
          disabled={pair.isPending}
          title="One line going out of one account and one coming into another, for the same amount"
          onClick={() => pair.mutate()}
        >
          {pair.isPending ? 'Pairing…' : 'Pair as a transfer'}
        </button>
      )}
      <input type="text" placeholder="Why leave out? (optional)" aria-label="Why leave these out" maxLength={200} value={reason} onChange={(e) => setReason(e.target.value)} />
      <button
        className="btn"
        type="button"
        disabled={out.isPending}
        title="Takes the selected lines out of every total without changing them. Find them again under Left out."
        onClick={() => out.mutate()}
      >
        {out.isPending ? 'Working…' : 'Leave out of the totals'}
      </button>
      <button className="btn" type="button" onClick={onDone}>
        Clear selection
      </button>
      {(apply.error ?? pair.error ?? out.error) && (
        <span className="problem" role="alert">
          {(apply.error ?? pair.error ?? out.error)!.message}
        </span>
      )}
    </form>
  )
}
