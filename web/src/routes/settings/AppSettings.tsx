import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { Problem, useAct } from './common'

interface AppSetting {
  key: string
  label: string
  help: string
  kind: 'number' | 'text'
  min: number
  max: number
  unit: string
  /** what is in force now */
  effective: string
  /** what the compose file (or environment) says */
  compose: string
  overridden: boolean
}

/**
 * A few options start from the compose file and can be changed here. What is saved here wins until it is reset to the compose value.
 * It applies at once, with no restart. Each tab shows the ones that belong to it.
 */
export function AppSettingsCard({ keys, title, intro }: { keys: string[]; title: string; intro: string }) {
  const settings = useQuery({ queryKey: ['settings', 'app'], queryFn: () => get<AppSetting[]>('/settings/app') })
  const { run, error, busy } = useAct()
  const [draft, setDraft] = useState<Record<string, string>>({})

  if (settings.isLoading) return <Loading what="settings" />
  if (settings.error || !settings.data) return <ErrorBox error={settings.error} />

  const rows = keys.map((k) => settings.data.find((s) => s.key === k)).filter((s): s is AppSetting => s !== undefined)
  const shown = (s: AppSetting) => draft[s.key] ?? s.effective
  const changed = rows.filter((s) => draft[s.key] !== undefined && draft[s.key] !== s.effective)
  const composeText = (s: AppSetting) => (s.compose === '' ? 'nothing (off)' : `${s.compose}${s.unit ? ` ${s.unit}` : ''}`)

  const save = async (e: FormEvent) => {
    e.preventDefault()
    const done = await run(() => send('PUT', '/settings/app', { values: Object.fromEntries(changed.map((s) => [s.key, draft[s.key].trim()])) }))
    if (done !== undefined) setDraft({})
  }

  return (
    <Card title={title}>
      <p className="muted">{intro}</p>
      <form className="form" onSubmit={save} noValidate>
        {rows.map((s) => (
          <label key={s.key}>
            <span>
              {s.label} {s.overridden && <Badge tone="info">changed here</Badge>}
            </span>
            <span className="inline-field">
              <input
                type={s.kind === 'number' ? 'number' : 'text'}
                inputMode={s.kind === 'number' ? 'numeric' : undefined}
                min={s.kind === 'number' ? s.min : undefined}
                max={s.kind === 'number' ? s.max : undefined}
                maxLength={s.kind === 'text' ? s.max : undefined}
                value={shown(s)}
                placeholder={s.kind === 'text' ? '/inbox' : undefined}
                onChange={(e) => setDraft({ ...draft, [s.key]: e.target.value })}
              />
              {s.unit && <span className="muted"> {s.unit}</span>}
            </span>
            <span className="muted">
              {s.help} The compose file says {composeText(s)}.
              {s.overridden && (
                <>
                  {' '}
                  <button
                    type="button"
                    className="linklike"
                    disabled={busy}
                    onClick={() => {
                      const next = { ...draft }
                      delete next[s.key]
                      setDraft(next)
                      void run(() => send('PUT', '/settings/app', { reset: [s.key] }))
                    }}
                  >
                    Use the compose value
                  </button>
                </>
              )}
            </span>
          </label>
        ))}
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || changed.length === 0}>
            {busy ? 'Saving…' : 'Save'}
          </button>
          {changed.length > 0 && (
            <button className="btn" type="button" onClick={() => setDraft({})}>
              Undo changes
            </button>
          )}
        </div>
        <Problem message={error} />
      </form>
    </Card>
  )
}
