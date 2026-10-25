import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { typeLabel } from '../../format'
import { Panel, Problem, TYPE_CHOICES, useAct } from './common'

interface CategoryRow {
  id: number
  name: string
  type: string
  isArchived: boolean
  mergedIntoId: number | null
  lineCount: number
}

interface TagRow {
  id: number
  name: string
  color: string | null
  lineCount: number
}

export function CategoriesSettings() {
  const categories = useQuery({ queryKey: ['settings', 'categories'], queryFn: () => get<CategoryRow[]>('/categories', { includeArchived: 'true' }) })
  const tags = useQuery({ queryKey: ['settings', 'tags'], queryFn: () => get<TagRow[]>('/tags') })
  const [category, setCategory] = useState<CategoryRow | 'new' | null>(null)
  const [tag, setTag] = useState<TagRow | 'new' | null>(null)

  if (categories.isLoading || tags.isLoading) return <Loading what="categories" />
  if (categories.error || tags.error || !categories.data || !tags.data) return <ErrorBox error={categories.error ?? tags.error} />

  return (
    <>
      {category && <CategoryForm key={category === 'new' ? 'new' : category.id} category={category === 'new' ? null : category} all={categories.data} onClose={() => setCategory(null)} />}
      <Card
        title="Categories"
        aside={
          <button type="button" className="btn primary" onClick={() => setCategory('new')}>
            Add a category
          </button>
        }
      >
        <p className="muted">A category's type is what its lines count as by default. Merge two categories to fold one into the other.</p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Default type</th>
                <th className="num">Lines</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {categories.data.map((c) => (
                <tr key={c.id}>
                  <td>
                    <Link to={`/lines?category=${c.id}`}>{c.name}</Link> {c.isArchived && <Badge tone="muted">{c.mergedIntoId ? 'merged' : 'archived'}</Badge>}
                  </td>
                  <td>{typeLabel(c.type)}</td>
                  <td className="num">{c.lineCount}</td>
                  <td>
                    <button type="button" className="btn" onClick={() => setCategory(c)}>
                      Change
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {tag && <TagForm key={tag === 'new' ? 'new' : tag.id} tag={tag === 'new' ? null : tag} onClose={() => setTag(null)} />}
      <Card
        title="Tags"
        aside={
          <button type="button" className="btn primary" onClick={() => setTag('new')}>
            Add a tag
          </button>
        }
      >
        <p className="muted">Tags are labels you put on lines, like a trip or a person. Tags are lower case with dashes.</p>
        <div className="chips">
          {tags.data.length === 0 && <span className="muted">No tags yet.</span>}
          {tags.data.map((t) => (
            <button key={t.id} type="button" className="chip" onClick={() => setTag(t)} style={t.color ? { borderColor: t.color } : undefined}>
              {t.name} · {t.lineCount}
            </button>
          ))}
        </div>
      </Card>
    </>
  )
}

function CategoryForm({ category, all, onClose }: { category: CategoryRow | null; all: CategoryRow[]; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(category?.name ?? '')
  const [type, setType] = useState(category?.type ?? 'extra')
  const [archived, setArchived] = useState(category?.isArchived ?? false)
  const [into, setInto] = useState('')

  const targets = all.filter((c) => !c.isArchived && c.id !== category?.id)

  return (
    <Panel title={category ? `Change ${category.name}` : 'Add a category'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          const body = { name: name.trim(), defaultType: type, ...(category ? { isArchived: archived } : {}) }
          void run(() => (category ? send('PATCH', `/categories/${category.id}`, body) : send('POST', '/categories', body)))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Counts as
          <select value={type} onChange={(e) => setType(e.target.value)}>
            {TYPE_CHOICES.map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </label>
        {category && (
          <label>
            <span>
              <input type="checkbox" checked={archived} onChange={(e) => setArchived(e.target.checked)} /> Archived (hidden from pickers; its lines keep it)
            </span>
          </label>
        )}
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || name.trim() === ''}>
            Save
          </button>
        </div>
      </form>
      {category && !category.isArchived && (
        <div className="form" style={{ marginTop: 16 }}>
          <label>
            Merge into another category: every line, split, rule and recurring item moves, then this one is archived
            <select value={into} onChange={(e) => setInto(e.target.value)}>
              <option value="">Pick the category to keep</option>
              {targets.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </label>
          <div className="toolbar">
            <button
              type="button"
              className="btn"
              disabled={busy || into === ''}
              onClick={() => {
                const keep = targets.find((c) => String(c.id) === into)
                if (keep && window.confirm(`Move everything from ${category.name} into ${keep.name}?`)) void run(() => send('POST', `/categories/${category.id}/merge`, { intoId: keep.id }))
              }}
            >
              Merge
            </button>
          </div>
        </div>
      )}
    </Panel>
  )
}

function TagForm({ tag, onClose }: { tag: TagRow | null; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(tag?.name ?? '')
  const [color, setColor] = useState(tag?.color ?? '')

  return (
    <Panel title={tag ? `Change ${tag.name}` : 'Add a tag'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          const body = { name: name.trim(), color: color.trim() || null }
          void run(() => (tag ? send('PATCH', `/tags/${tag.id}`, body) : send('POST', '/tags', body)))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Colour (like #336699, optional)
          <input value={color} onChange={(e) => setColor(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || name.trim() === ''}>
            Save
          </button>
          {tag && (
            <>
              <Link className="btn" to={`/lines?tag=${encodeURIComponent(tag.name)}`}>
                Show its lines
              </Link>
              <button
                type="button"
                className="btn"
                disabled={busy}
                onClick={() => {
                  if (window.confirm(`Delete the tag ${tag.name}? It is taken off ${tag.lineCount} line${tag.lineCount === 1 ? '' : 's'}; nothing else changes.`)) void run(() => send('DELETE', `/tags/${tag.id}`))
                }}
              >
                Delete
              </button>
            </>
          )}
        </div>
      </form>
    </Panel>
  )
}
