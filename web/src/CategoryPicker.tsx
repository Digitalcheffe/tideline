import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { send, type CategoryInfo } from './api'
import { typeLabel } from './format'

const RECENT_KEY = 'tideline.recent-categories'
const TYPES = ['income', 'bill', 'required', 'extra', 'one_time', 'savings']

function readRecent(): number[] {
  try {
    const v = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]')
    return Array.isArray(v) ? v.filter((x) => Number.isInteger(x)) : []
  } catch {
    return []
  }
}

/** The categories used last, newest first. Kept only in this browser, as a convenience. */
function remember(id: number) {
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify([id, ...readRecent().filter((x) => x !== id)].slice(0, 8)))
  } catch {
    /* no storage: the list simply has no recent ones */
  }
}

/**
 * Pick a category by typing a few letters instead of scrolling a long list. The ones used last come first. When nothing fits, an owner can make
 * the category right here and say what type it counts as, without leaving the screen.
 */
export function CategoryPicker({
  categories, value, onChange, canCreate, label, noneLabel,
}: {
  categories: CategoryInfo[]
  value: string
  onChange: (id: string, category: CategoryInfo | null) => void
  canCreate: boolean
  label: string
  /** When given, an entry with this text clears the choice. */
  noneLabel?: string
}) {
  const client = useQueryClient()
  const box = useRef<HTMLInputElement>(null)
  const pop = useRef<HTMLDivElement>(null)
  const [made, setMade] = useState<CategoryInfo | null>(null)
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [creating, setCreating] = useState(false)
  const [newType, setNewType] = useState('extra')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [at, setAt] = useState<{ top: number; left: number; width: number } | null>(null)

  const selected = categories.find((c) => String(c.id) === value) ?? (made && String(made.id) === value ? made : null)

  // the list is drawn at a fixed place so a table or card around it cannot clip it; it closes if the page moves
  useEffect(() => {
    if (!open) return
    // scrolling the list itself must not close it; only the page or drawer moving underneath it does
    const close = (e: Event) => {
      if (e.target instanceof Node && pop.current?.contains(e.target)) return
      setOpen(false)
    }
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [open])

  const text = query.trim().toLowerCase()
  const recent = readRecent()
    .map((id) => categories.find((c) => c.id === id))
    .filter((c): c is CategoryInfo => c !== undefined)
  const choices: (CategoryInfo | 'none' | 'create')[] = []
  if (text === '') {
    if (noneLabel) choices.push('none')
    choices.push(...recent, ...categories.filter((c) => !recent.includes(c)))
  } else {
    const hits = categories.filter((c) => c.name.toLowerCase().includes(text))
    choices.push(...hits.filter((c) => c.name.toLowerCase().startsWith(text)), ...hits.filter((c) => !c.name.toLowerCase().startsWith(text)))
    if (canCreate && !categories.some((c) => c.name.toLowerCase() === text)) choices.push('create')
  }

  const pick = (c: CategoryInfo | 'none') => {
    if (c === 'none') onChange('', null)
    else {
      remember(c.id)
      onChange(String(c.id), c)
    }
    setOpen(false)
    setCreating(false)
    setQuery('')
  }

  const create = async () => {
    setBusy(true)
    setError(null)
    try {
      const name = query.trim()
      const result = await send<{ id: number }>('POST', '/categories', { name, defaultType: newType })
      const category = { id: result.id, name, type: newType }
      setMade(category)
      await client.invalidateQueries({ queryKey: ['categories'] })
      pick(category)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const choose = (c: CategoryInfo | 'none' | 'create' | undefined) => {
    if (c === undefined) return
    if (c === 'create') {
      setCreating(true)
      setError(null)
    } else pick(c)
  }

  const show = () => {
    const r = box.current?.getBoundingClientRect()
    if (r) setAt({ top: r.bottom + 2, left: r.left, width: Math.max(r.width, 260) })
    setOpen(true)
  }

  return (
    <div className="picker">
      <input
        ref={box}
        type="text"
        role="combobox"
        aria-label={label}
        aria-expanded={open}
        aria-autocomplete="list"
        autoComplete="off"
        placeholder={selected ? selected.name : 'Type to find a category…'}
        value={open ? query : (selected?.name ?? '')}
        onFocus={show}
        onClick={show}
        onChange={(e) => {
          setQuery(e.target.value)
          setActive(0)
          setCreating(false)
          if (!open) show()
        }}
        onBlur={() => window.setTimeout(() => setOpen(false), 120)}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault()
            setActive((a) => Math.min(a + 1, choices.length - 1))
          } else if (e.key === 'ArrowUp') {
            e.preventDefault()
            setActive((a) => Math.max(a - 1, 0))
          } else if (e.key === 'Enter') {
            if (open) {
              e.preventDefault()
              choose(choices[active])
            }
          } else if (e.key === 'Escape' && open) {
            e.stopPropagation()
            setOpen(false)
          }
        }}
      />
      {open && at && (
        <div className="picker-pop" ref={pop}
          style={{ top: at.top, left: at.left, width: at.width }}
          onMouseDown={(e) => e.preventDefault()}
          // inside a form label, a click here would be passed on to the box and open the list again
          onClick={(e) => e.preventDefault()}>
          {creating ? (
            <div className="picker-create">
              <p>
                New category <strong>{query.trim()}</strong>. What does it usually count as?
              </p>
              <select aria-label="Type for the new category" value={newType} onChange={(e) => setNewType(e.target.value)}>
                {TYPES.map((t) => (
                  <option key={t} value={t}>
                    {typeLabel(t)}
                  </option>
                ))}
              </select>
              <div className="toolbar">
                <button type="button" className="btn primary" disabled={busy} onClick={() => void create()}>
                  {busy ? 'Making…' : 'Make it and use it'}
                </button>
                <button type="button" className="btn" onClick={() => setCreating(false)}>
                  Back
                </button>
              </div>
              {error && (
                <p className="problem" role="alert">
                  {error}
                </p>
              )}
            </div>
          ) : (
            <ul role="listbox" aria-label={`${label} choices`}>
              {choices.length === 0 && <li className="muted">Nothing matches.</li>}
              {choices.map((c, i) => (
                <li
                  key={c === 'none' ? 'none' : c === 'create' ? 'create' : c.id}
                  role="option"
                  aria-selected={i === active}
                  className={i === active ? 'active' : ''}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => choose(c)}
                >
                  {c === 'none' ? (
                    <span>{noneLabel}</span>
                  ) : c === 'create' ? (
                    <span>
                      Make a new category “{query.trim()}”
                    </span>
                  ) : (
                    <>
                      <span>{c.name}</span>
                      <span className="muted">{typeLabel(c.type)}</span>
                    </>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}
