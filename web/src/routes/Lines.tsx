import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { useMe } from '../auth'
import { getLines, get, send, type AccountInfo, type CategoryInfo, type Line } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading, Money } from '../components'
import { dateLabel, dollars, parseDollars, ruleParams, ruleWords, typeLabel } from '../format'
import { usePeriod } from '../period'
import { CategoryPicker } from '../CategoryPicker'
import { AnswerCell } from './NeedsAnswer'
import { TransactionDrawer } from './TransactionDrawer'

const SORTS: { key: string; label: string }[] = [
  { key: 'date_desc', label: 'Newest first' },
  { key: 'date_asc', label: 'Oldest first' },
  { key: 'amount_desc', label: 'Largest in first' },
  { key: 'amount_asc', label: 'Largest out first' },
]

/**
 * The ledger: every line, for the period in the menu. It is also the drill-down view that every number on every screen
 * opens (docs/ARCHITECTURE.md), so the filters live in the URL.
 */
export function Lines({ needsMode = false }: { needsMode?: boolean } = {}) {
  const [params, setParams] = useSearchParams()
  const period = usePeriod()
  const me = useMe().user
  const isOwner = me.role === 'owner' // making a rule is an owner's job
  const canAnswer = me.role !== 'viewer'
  // what the last action did, said under the filters; a pairing can be undone from it
  const [notice, setNotice] = useState<{ text: string; undoMove?: number } | null>(null)
  const setMessage = (text: string) => setNotice({ text })
  const undoPair = useMutation({
    mutationFn: (moveId: number) => send('DELETE', `/moves/${moveId}`),
    onSuccess: () => {
      setNotice({ text: 'Unpaired. The two lines are ordinary lines again.' })
      queryClient.invalidateQueries()
    },
  })
  const queryClient = useQueryClient()
  const open = params.get('tx')
  const apiParams = new URLSearchParams(params)
  apiParams.delete('tx')
  // the pile waiting for a category is a to-do list, not a period: oldest first, no dates
  if (needsMode) {
    apiParams.set('needs', 'true')
    apiParams.set('from', '1900-01-01')
    apiParams.set('to', '2999-12-31')
    if (!apiParams.has('sort')) apiParams.set('sort', 'date_asc')
  }
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
  // the last four digits of each account (the only part Tideline keeps), shown small under its name so look-alike accounts can be told apart
  const maskOf = new Map((accounts.data ?? []).map((a) => [a.id, a.bankNumberMask] as const))

  const set =(key: string, value: string | null) => {
    const next = new URLSearchParams(params)
    if (value === null || value === '') next.delete(key)
    else next.set(key, value)
    if (key !== 'page') next.delete('page')
    setParams(next)
  }

  const setMany = (entries: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [key, value] of Object.entries(entries)) {
      if (value === null || value === '') next.delete(key)
      else next.set(key, value)
    }
    next.delete('page')
    setParams(next)
  }

  /** The search box: words search the description, payee and notes; a lone amount like 45.20 finds lines of exactly that size, in or out. */
  const search = (text: string) => {
    const t = text.trim()
    const cents = /^\$?\s*\d[\d,]*(\.\d{1,2})?$/.test(t) ? parseDollars(t) : null
    if (cents !== null && cents > 0) setMany({ q: null, minCents: String(cents), maxCents: String(cents) })
    else set('q', t)
  }

  /** An amount box: dollars in, whole cents in the address. Empty clears it. */
  const amountBox = (key: 'minCents' | 'maxCents', text: string) => {
    const t = text.trim()
    const cents = t === '' ? null : parseDollars(t)
    if (t !== '' && (cents === null || cents < 0)) return
    const next = cents === null ? '' : String(cents)
    if (next !== (apiParams.get(key) ?? '')) set(key, next)
  }

  const filterKeys = ['type', 'category', 'account', 'asset', 'tag', 'review', 'needs', 'recurring', 'direction', 'q', 'minCents', 'maxCents', 'move', 'leftOut']
  const needsView = apiParams.get('needs') === 'true'
  const leftOutView = apiParams.get('leftOut') === 'true'
  const filtersActive = filterKeys.some((k) => apiParams.has(k) && !(needsMode && k === 'needs'))
  const clearFilters = () => {
    const next = new URLSearchParams(params)
    filterKeys.forEach((k) => next.delete(k))
    next.delete('page')
    setParams(next)
  }

  const chips: string[] = []
  const accountName = (id: string) => accounts.data?.find((a) => String(a.id) === id)?.name ?? `account ${id}`
  const categoryName = (id: string) => (id === '0' ? 'No category' : (categories.data?.find((c) => String(c.id) === id)?.name ?? `category ${id}`))
  // the pile of lines waiting for a category is not a period, so it has no dates to show
  if (needsView) chips.push('needs a category')
  else if (apiParams.get('from') === '1900-01-01' && apiParams.get('to') === '2999-12-31') chips.push('all dates')
  else chips.push(`${dateLabel(apiParams.get('from')!)} to ${dateLabel(apiParams.get('to')!)}`)
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
  if (apiParams.get('minCents') && apiParams.get('minCents') === apiParams.get('maxCents')) chips.push(`exactly ${dollars(Number(apiParams.get('minCents')))}`)
  else {
    if (apiParams.get('minCents')) chips.push(`at least ${dollars(Number(apiParams.get('minCents')))}`)
    if (apiParams.get('maxCents')) chips.push(`at most ${dollars(Number(apiParams.get('maxCents')))}`)
  }

  return (
    <>
      <h1>{needsMode ? 'Needs a category' : 'Ledger'}</h1>
      {needsMode && (
        <p className="muted">
          Lines the rules could not place, and checks, which the bank only gives as a number. Pick a category on a row and press Answer, or tick several lines and set their category together.
          {isOwner && ' Answer and make a rule teaches Tideline the line’s words, so the next ones sort themselves.'}
        </p>
      )}
      <div className="chips" aria-label="Filters">
        {chips.map((c) => (
          <span className="chip" key={c}>
            {c}
          </span>
        ))}
      </div>

      <div className="filterbar">
      <div className="row">
        <input
          className="grow"
          type="search"
          placeholder="Search words, or an amount like 45.20"
          aria-label="Search"
          defaultValue={apiParams.get('q') ?? ''}
          key={`q-${apiParams.get('q') ?? ''}`}
          onKeyDown={(e) => e.key === 'Enter' && search((e.target as HTMLInputElement).value)}
        />
        <select aria-label="Sort" value={apiParams.get('sort') ?? 'date_desc'} onChange={(e) => set('sort', e.target.value)}>
          {SORTS.map((s) => (
            <option key={s.key} value={s.key}>
              {s.label}
            </option>
          ))}
        </select>
      </div>
      <div className="row">
        <select aria-label="Account" value={apiParams.get('account') ?? ''} onChange={(e) => set('account', e.target.value)}>
          <option value="">All accounts</option>
          {accounts.data?.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </select>
        {!needsMode && (
          <>
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
          </>
        )}
        <select aria-label="Direction" value={apiParams.get('direction') ?? ''} onChange={(e) => set('direction', e.target.value)}>
          <option value="">In and out</option>
          <option value="in">Money in</option>
          <option value="out">Money out</option>
        </select>
        {(['minCents', 'maxCents'] as const).map((key) => (
          <input
            key={`${key}-${apiParams.get(key) ?? ''}`}
            className="amount-box"
            type="text"
            inputMode="decimal"
            aria-label={key === 'minCents' ? 'Amount from' : 'Amount to'}
            placeholder={key === 'minCents' ? '$ from' : '$ to'}
            title="Size of the line in dollars, whether it came in or went out"
            defaultValue={apiParams.get(key) ? (Number(apiParams.get(key)) / 100).toFixed(2) : ''}
            onKeyDown={(e) => e.key === 'Enter' && amountBox(key, (e.target as HTMLInputElement).value)}
            onBlur={(e) => amountBox(key, e.target.value)}
          />
        ))}
        {filtersActive && (
          <button className="btn spacer" type="button" onClick={clearFilters}>
            Clear filters
          </button>
        )}
      </div>
      {!needsMode && (
        <div className="row checks">
          <label>
            <input type="checkbox" checked={apiParams.get('review') === 'true'} onChange={(e) => set('review', e.target.checked ? 'true' : null)} /> To review
          </label>
          <label>
            <input type="checkbox" checked={apiParams.get('move') === 'include'} disabled={leftOutView} onChange={(e) => set('move', e.target.checked ? 'include' : null)} /> Show transfers
          </label>
          <label title="Lines you took out of every total, such as a duplicate. Open one to bring it back.">
            <input type="checkbox" checked={leftOutView} onChange={(e) => set('leftOut', e.target.checked ? 'true' : null)} /> Left out
          </label>
        </div>
      )}
      </div>
      {notice && (
        <p role="status">
          {notice.text}{' '}
          {notice.undoMove !== undefined && (
            <>
              <button type="button" className="btn" disabled={undoPair.isPending} onClick={() => undoPair.mutate(notice.undoMove!)}>
                Undo
              </button>{' '}
              <button type="button" className="btn" onClick={() => set('move', 'include')} title="Transfers are left out of this list. Show them to see the pair.">
                Show transfers
              </button>
            </>
          )}
          {undoPair.error && <span className="problem"> {undoPair.error.message}</span>}
        </p>
      )}

      <Card
        title={lines.data ? `${lines.data.summary.count} ${lines.data.summary.count === 1 ? 'line' : 'lines'}` : 'Lines'}
        aside={lines.data && <>Total <Money cents={lines.data.summary.sumCents} /></>}
      >
        {lines.isLoading && <Loading what="lines" />}
        {lines.error && <ErrorBox error={lines.error} />}
        {lines.data && lines.data.items.length === 0 && <Empty>{leftOutView ? 'Nothing is left out.' : 'No lines match.'}</Empty>}
        {isOwner && (apiParams.get('review') === 'true' || needsView) && lines.data && lines.data.items.length > 0 && (
          <p className="muted">
            Press <strong>Make a rule</strong> beside a line to teach Tideline its words, so the next one sorts itself. You pick the category, and can try the rule on past lines before you save it.
          </p>
        )}
        {selected.size > 0 && categories.data && (
          <BulkBar
            key={[...selected].sort((a, b) => a - b).join(',')}
            ids={[...selected]}
            lines={(lines.data?.items ?? []).filter((l) => selected.has(l.transactionId))}
            categories={categories.data}
            leftOut={leftOutView}
            onDone={() => setSelected(new Set())}
            onMessage={(text, undoMove) => setNotice({ text, undoMove })}
          />
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
                  <th>{needsMode ? 'What was it?' : 'Meaning'}</th>
                  <th className="num">Amount</th>
                  <th aria-label="Open" />
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
                      {maskOf.get(l.accountId) && <span className="cell-note">{maskOf.get(l.accountId)}</span>}
                    </td>
                    <td>
                      <button
                        type="button"
                        className="rowlink"
                        title="Open this line to see and change it"
                        onClick={(e) => {
                          e.stopPropagation()
                          set('tx', String(l.transactionId))
                        }}
                      >
                        {l.description}
                      </button>
                      {l.payee && l.payee !== l.description && (
                        <span className="cell-note">
                          <Pick onPick={() => set('q', l.payee)} title={`Search for ${l.payee}`}>
                            {l.payee}
                          </Pick>
                        </span>
                      )}
                      {l.isSplitPart && <Badge tone="info">Split part</Badge>}
                      {isOwner && l.type && !l.isSplitPart && (l.tags.includes('review') || l.categoryId === null || l.categoryName === 'Uncategorized') && (
                        <span className="cell-note">
                          <Link
                            className="pick"
                            title={/^check #/i.test(l.description) ? 'Start a rule for checks of this amount' : `Start a rule for lines like “${ruleWords(l.description)}”`}
                            to={`/settings/rules?${new URLSearchParams({ new: '1', ...ruleParams(l.description, l.amountCents), ...(l.payee && !/^check #/i.test(l.description) ? { payee: l.payee } : {}) }).toString()}`}
                            onClick={(e) => e.stopPropagation()}
                          >
                            Make a rule
                          </Link>
                        </span>
                      )}
                    </td>
                    <td onClick={needsMode && canAnswer && !l.isSplitPart ? (e) => e.stopPropagation() : undefined}>
                      {needsMode && canAnswer && l.type && !l.isSplitPart ? (
                        <AnswerCell line={l} categories={(categories.data ?? []).filter((c) => c.name !== 'Uncategorized')} isOwner={isOwner} onDone={setMessage} onOpen={() => set('tx', String(l.transactionId))} />
                      ) : l.type ? (
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
                    <td className="chevron" aria-hidden="true">
                      ›
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
function BulkBar({
  ids, lines, categories, leftOut, onDone, onMessage,
}: { ids: number[]; lines: Line[]; categories: CategoryInfo[]; leftOut: boolean; onDone: () => void; onMessage: (message: string, undoMove?: number) => void }) {
  const client = useQueryClient()
  const canCreate = useMe().user.role === 'owner'
  const [categoryId, setCategoryId] = useState('')
  const [type, setType] = useState('')
  const [addTags, setAddTags] = useState('')
  const [removeTags, setRemoveTags] = useState('')
  const [assetId, setAssetId] = useState('')
  const [recurringId, setRecurringId] = useState('')
  const assetChoices = useQuery({ queryKey: ['accounts'], queryFn: () => get<AccountInfo[]>('/accounts') }).data?.filter((a) => a.kind === 'asset') ?? []
  const recurringChoices = useQuery({ queryKey: ['settings', 'recurring'], queryFn: () => get<{ id: number; name: string }[]>('/recurring') }).data ?? []

  const words = (text: string) => text.split(',').map((x) => x.trim()).filter(Boolean)
  const nothing = !categoryId && !type && !assetId && !recurringId && words(addTags).length === 0 && words(removeTags).length === 0

  const apply = useMutation({
    mutationFn: () =>
      send<{ updated: number; skippedTransfers: number; notFound: number }>('POST', '/transactions/bulk', {
        ids,
        set: {
          categoryId: categoryId ? Number(categoryId) : undefined,
          type: type || undefined,
          assetId: assetId ? Number(assetId) : undefined,
          recurringId: recurringId ? Number(recurringId) : undefined,
          addTags: words(addTags),
          removeTags: words(removeTags),
        },
      }),
    onSuccess: () => {
      client.invalidateQueries()
      onDone()
    },
  })

  // two lines that look like the two sides of a transfer: out of one account, into another, the same amount
  const [outLine, inLine] = lines.length === 2 && lines[0].amountCents < 0 ? [lines[0], lines[1]] : [lines[1], lines[0]]
  const looksLikeTransfer =
    lines.length === 2 && outLine.amountCents < 0 && inLine.amountCents === -outLine.amountCents && outLine.accountId !== inLine.accountId
  // an owner is asked to remember it unless they say otherwise; the words show at once so they can be fixed or the box cleared
  const [remember, setRemember] = useState(canCreate)
  const [applyPast, setApplyPast] = useState(true)
  // the whole text of each line to start with; delete the parts that change from one transfer to the next (a date, a number) to match more
  const [fromWords, setFromWords] = useState(looksLikeTransfer ? outLine.description : '')
  const [toWords, setToWords] = useState(looksLikeTransfer ? inLine.description : '')

  const pair = useMutation({
    mutationFn: async (): Promise<{ text: string; undoMove?: number }> => {
      const made = await send<{ id: number }>('POST', '/moves', { transactionIds: ids })
      // the pair leaves this list (transfers are not shown unless asked for), so say where it went and offer the way back
      if (!remember || !looksLikeTransfer) return { text: 'Paired as a transfer. It is left out of this list; Show transfers brings it back into view.', undoMove: made.id }
      try {
        const pattern = await send<{ id: number }>('POST', '/move-patterns', {
          fromAccountId: outLine.accountId, fromContains: fromWords.trim(), toAccountId: inLine.accountId, toContains: toWords.trim(),
        })
        if (!applyPast) return { text: `Paired as a transfer, and remembered for next time (${outLine.accountName} to ${inLine.accountName}). The pattern is in Settings, Accounts.` }
        const done = await send<{ paired: number; skipped: number }>('POST', `/move-patterns/${pattern.id}/apply`)
        return {
          text: `Paired as a transfer, and remembered for next time. ${done.paired === 0 ? 'No other lines in the ledger matched it.' : `${done.paired} other ${done.paired === 1 ? 'pair' : 'pairs'} in the ledger paired too.`}${done.skipped > 0 ? ` ${done.skipped} could not be paired (a split line, for example).` : ''} The pattern is in Settings, Accounts.`,
        }
      } catch (e) {
        return { text: `Paired as a transfer, but the pattern was not saved: ${e instanceof Error ? e.message : String(e)}`, undoMove: made.id }
      }
    },
    onSuccess: (result) => {
      client.invalidateQueries()
      onMessage(result.text, result.undoMove)
      onDone()
    },
  })

  const pickCategory = (value: string, made: CategoryInfo | null = null) => {
    setCategoryId(value)
    const chosen = made ?? categories.find((c) => String(c.id) === value)
    if (chosen && !type) setType(chosen.type)
  }

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
      <CategoryPicker categories={categories} value={categoryId} label="Set category" canCreate={canCreate} noneLabel="Category: no change" onChange={pickCategory} />
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
      <select aria-label="Link to a recurring item" value={recurringId} onChange={(e) => setRecurringId(e.target.value)}>
        <option value="">Bill or subscription: no change</option>
        <option value="0">Bill or subscription: none</option>
        {recurringChoices.map((r) => (
          <option key={r.id} value={r.id}>
            {r.name}
          </option>
        ))}
      </select>
      <input type="text" placeholder="Add tags" aria-label="Add tags" value={addTags} onChange={(e) => setAddTags(e.target.value)} />
      <input type="text" placeholder="Remove tags" aria-label="Remove tags" value={removeTags} onChange={(e) => setRemoveTags(e.target.value)} />
      <button className="btn primary" type="submit" disabled={nothing || apply.isPending}>
        {apply.isPending ? 'Saving…' : 'Apply'}
      </button>
      {ids.length === 2 && (
        <button
          className="btn"
          type="button"
          disabled={pair.isPending || (remember && (fromWords.trim() === '' || toWords.trim() === ''))}
          title="One line going out of one account and one coming into another, for the same amount"
          onClick={() => pair.mutate()}
        >
          {pair.isPending ? 'Pairing…' : 'Pair as a transfer'}
        </button>
      )}
      {ids.length === 2 && canCreate && looksLikeTransfer && (
        <label title="Save a transfer pattern from these two lines, so the next ones pair themselves when they are imported">
          <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> Remember this for next time
        </label>
      )}
      {ids.length === 2 && canCreate && looksLikeTransfer && (
        <div className={`pattern-bits ${remember ? '' : 'off'}`}>
          <p className="muted pattern-help">
            Next time, a line with these words is paired with one from the other account for the same amount. These are the full descriptions. Delete anything that changes
            each time, such as a date or a number, so more lines match.
          </p>
          <label className="pattern-words">
            Out of {outLine.accountName}, the line says
            <input type="text" aria-label="Words on the line going out" value={fromWords} disabled={!remember} onChange={(e) => setFromWords(e.target.value)} />
          </label>
          <label className="pattern-words">
            Into {inLine.accountName}, the line says
            <input type="text" aria-label="Words on the line coming in" value={toWords} disabled={!remember} onChange={(e) => setToWords(e.target.value)} />
          </label>
          <label>
            <input type="checkbox" checked={applyPast} disabled={!remember} onChange={(e) => setApplyPast(e.target.checked)} /> Also pair the other matching lines already in the ledger
          </label>
        </div>
      )}
      <Link className="btn" to={`/settings/recurring?lines=${ids.slice(0, 200).join(',')}`} title="Start a new bill or subscription from the selected lines">
        Make a recurring item
      </Link>
      <button className="btn" type="button" onClick={onDone}>
        Clear selection
      </button>
      {(apply.error ?? pair.error) && (
        <span className="problem" role="alert">
          {(apply.error ?? pair.error)!.message}
        </span>
      )}
    </form>
  )
}
