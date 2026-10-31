import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMe } from '../auth'
import { get, send, type AccountInfo, type CategoryInfo, type TransactionDetail } from '../api'
import { Badge } from '../components'
import { CategoryPicker } from '../CategoryPicker'
import { dollars, parseDollars, ruleParams, typeLabel } from '../format'

const TYPES = ['income', 'bill', 'required', 'extra', 'one_time', 'savings']

function useCategories() {
  return useQuery({ queryKey: ['categories'], queryFn: () => get<CategoryInfo[]>('/categories') })
}

/** Editing a line: what it means, and how it is split. The bank fields above it never change. */
export function EditPanel({ t, onSaved }: { t: TransactionDetail; onSaved: () => void }) {
  return (
    <>
      <MeaningForm t={t} onSaved={onSaved} />
      <details className="fold" open={t.splits.length > 0}>
        <summary>Split this line{t.splits.length > 0 ? ` (${t.splits.length})` : ''}</summary>
        <SplitsForm t={t} />
      </details>
    </>
  )
}

function useRefresh() {
  const client = useQueryClient()
  return () => client.invalidateQueries()
}

function MeaningForm({ t, onSaved }: { t: TransactionDetail; onSaved: () => void }) {
  const categories = useCategories()
  const refresh = useRefresh()
  const isOwner = useMe().user.role === 'owner'

  const [categoryId, setCategoryId] = useState<string>(t.categoryId ? String(t.categoryId) : '')
  const [type, setType] = useState(t.type ?? 'extra')
  const [payee, setPayee] = useState(t.payee ?? '')
  const [tags, setTags] = useState(t.tags.join(', '))
  const [notes, setNotes] = useState(t.notes ?? '')
  const [planId, setPlanId] = useState(t.planId ? String(t.planId) : '')
  const plans = useQuery({ queryKey: ['plans-pick'], queryFn: () => get<{ id: number; name: string; lifecycle: string }[]>('/plans') })
  const [assetId, setAssetId] = useState<string>(t.asset ? String(t.asset.id) : '')
  const [recurringId, setRecurringId] = useState<string>(t.recurring ? String(t.recurring.id) : '')
  const items = useQuery({ queryKey: ['settings', 'recurring'], queryFn: () => get<{ id: number; name: string }[]>('/recurring') })
  const assets = useQuery({ queryKey: ['accounts'], queryFn: () => get<AccountInfo[]>('/accounts') })
  const assetChoices = assets.data?.filter((a) => a.kind === 'asset') ?? []

  const save = useMutation({
    mutationFn: () =>
      send('PATCH', `/transactions/${t.id}`, {
        version: t.version,
        planId: planId ? Number(planId) : 0,
        assetId: assetId ? Number(assetId) : 0,
        recurringId: recurringId ? Number(recurringId) : 0,
        categoryId: categoryId ? Number(categoryId) : 0,
        type,
        payee,
        notes,
        tags: tags.split(',').map((x) => x.trim()).filter(Boolean),
      }),
    // the lists behind the drawer update, and the drawer closes: the change shows in the row
    onSuccess: async () => {
      await refresh()
      onSaved()
    },
  })
  const reset = useMutation({ mutationFn: () => send('POST', `/transactions/${t.id}/reset-to-rules`), onSuccess: refresh })

  const pickCategory = (value: string, made: CategoryInfo | null = null) => {
    setCategoryId(value)
    const chosen = made ?? categories.data?.find((c) => String(c.id) === value)
    if (chosen) setType(chosen.type)
    // a real category answers "review", so the tag goes (the tags box is still yours to edit before you save)
    if (chosen && chosen.name !== 'Uncategorized') setTags(tags.split(',').map((x) => x.trim()).filter((x) => x && x !== 'review').join(', '))
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      <div className="form two">
        <label>
          Category
          <CategoryPicker categories={categories.data ?? []} value={categoryId} label="Category" canCreate={isOwner} noneLabel="No category" onChange={pickCategory} />
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
          Tags <span className="muted">(commas)</span>
          <input type="text" value={tags} onChange={(e) => setTags(e.target.value)} />
        </label>
        {((plans.data?.length ?? 0) > 0 || planId !== '') && (
          <label>
            Plan this counts toward
            <select value={planId} onChange={(e) => setPlanId(e.target.value)}>
              <option value="">None</option>
              {plans.data?.filter((p) => p.lifecycle === 'active' || String(p.id) === planId).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
        )}
        {(assetChoices.length > 0 || assetId !== '') && (
          <label>
            Asset this was spent on
            <select value={assetId} onChange={(e) => setAssetId(e.target.value)}>
              <option value="">None</option>
              {assetChoices.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label>
          Recurring bill or subscription
          <select value={recurringId} onChange={(e) => setRecurringId(e.target.value)}>
            <option value="">None</option>
            {items.data?.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
              </option>
            ))}
          </select>
          {recurringId === '' && (
            <span className="muted">
              Not in the list? <Link to={`/settings/recurring?lines=${t.id}`}>Make one from this line</Link>
            </span>
          )}
        </label>
        <label className="wide">
          Notes
          <textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
      </div>
      {(save.error || reset.error) && <p className="problem" role="alert">{String((save.error ?? reset.error)?.message)}</p>}
      <div className="savebar">
        <button className="btn primary" type="submit" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        {t.isManualOverride && (
          <button className="btn" type="button" disabled={reset.isPending} onClick={() => reset.mutate()} title="Let the rules decide again">
            Reset to rules
          </button>
        )}
        {isOwner && (
          <Link
            className="btn"
            title="Start a rule from this line and what you have picked above"
            to={`/settings/rules?${new URLSearchParams({
              new: '1',
              ...ruleParams(t.bank.description, t.bank.amountCents),
              ...(categoryId ? { category: categoryId } : {}),
              type,
              ...(payee ? { payee } : {}),
              ...(tags.trim() ? { tags } : {}),
            }).toString()}`}
          >
            Create rule from this
          </Link>
        )}
      </div>
    </form>
  )
}

interface SplitDraft {
  amount: string
  categoryId: string
  type: string
  note: string
}

function SplitsForm({ t }: { t: TransactionDetail }) {
  const categories = useCategories()
  const refresh = useRefresh()
  const sign = t.bank.amountCents < 0 ? -1 : 1

  const [drafts, setDrafts] = useState<SplitDraft[]>(
    t.splits.map((s) => ({ amount: (Math.abs(s.amountCents) / 100).toFixed(2), categoryId: String(s.categoryId), type: s.type, note: s.note ?? '' })),
  )

  const cents = drafts.map((d) => parseDollars(d.amount))
  const used = cents.reduce<number>((sum, c) => sum + (c ?? 0), 0)
  const left = Math.abs(t.bank.amountCents) - used
  const valid = drafts.every((d, i) => cents[i] !== null && cents[i]! > 0 && d.categoryId !== '') && left >= 0

  const save = useMutation({
    mutationFn: () =>
      send('PUT', `/transactions/${t.id}/splits`, {
        version: t.version,
        splits: drafts.map((d, i) => ({ amountCents: sign * cents[i]!, categoryId: Number(d.categoryId), type: d.type, note: d.note })),
      }),
    onSuccess: refresh,
  })

  const update = (i: number, patch: Partial<SplitDraft>) => setDrafts(drafts.map((d, j) => (j === i ? { ...d, ...patch } : d)))
  const pickCategory = (i: number, value: string) => {
    const chosen = categories.data?.find((c) => String(c.id) === value)
    update(i, { categoryId: value, ...(chosen ? { type: chosen.type } : {}) })
  }

  return (
    <>
      <p className="muted">
        Move part of it to another category or type. What you leave unassigned keeps the line's own category and type, and the total never changes.
      </p>
      {drafts.map((d, i) => (
        <div className="split-row" key={i}>
          <input type="text" inputMode="decimal" aria-label="Split amount" value={d.amount} onChange={(e) => update(i, { amount: e.target.value })} />
          <select aria-label="Split category" value={d.categoryId} onChange={(e) => pickCategory(i, e.target.value)}>
            <option value="">Category…</option>
            {categories.data?.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          <select aria-label="Split type" value={d.type} onChange={(e) => update(i, { type: e.target.value })}>
            {TYPES.map((x) => (
              <option key={x} value={x}>
                {typeLabel(x)}
              </option>
            ))}
          </select>
          <input type="text" placeholder="Note" aria-label="Split note" value={d.note} onChange={(e) => update(i, { note: e.target.value })} />
          <button className="btn" type="button" aria-label="Remove this split" onClick={() => setDrafts(drafts.filter((_, j) => j !== i))}>
            ✕
          </button>
        </div>
      ))}
      <p>
        Remaining unassigned: <strong className={left < 0 ? 'neg' : ''}>{dollars(left * sign)}</strong>
        {left < 0 && <> <Badge tone="bad">More than the line</Badge></>}
      </p>
      <div className="toolbar">
        <button className="btn" type="button" onClick={() => setDrafts([...drafts, { amount: '', categoryId: '', type: 'extra', note: '' }])}>
          Add a split
        </button>
        <button className="btn primary" type="button" disabled={!valid || save.isPending || (drafts.length === 0 && t.splits.length === 0)} onClick={() => save.mutate()}>
          {save.isPending ? 'Saving…' : drafts.length === 0 && t.splits.length > 0 ? 'Remove all splits' : 'Save splits'}
        </button>
      </div>
      {save.isSuccess && <p><Badge tone="good">Saved</Badge></p>}
      {save.error && <p className="problem" role="alert">{save.error.message}</p>}
    </>
  )
}
