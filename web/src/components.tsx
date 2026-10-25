import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { dollars, percent } from './format'

/** Every number on every screen is a link built with the one drill-down contract. */
export function drillPath(drill: string | null | undefined): string | null {
  return drill ? `/lines?${drill}` : null
}

export function Money({ cents, drill, className, title }: { cents: number; drill?: string | null; className?: string; title?: string }) {
  const text = dollars(cents)
  const to = drillPath(drill)
  const cls = `money ${cents < 0 ? 'neg' : ''} ${className ?? ''}`.trim()
  return to ? (
    <Link to={to} className={`${cls} drill`} title={title ?? 'Show the lines behind this number'}>
      {text}
    </Link>
  ) : (
    <span className={cls} title={title}>
      {text}
    </span>
  )
}

export function Card({ title, aside, children, className }: { title?: ReactNode; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`card ${className ?? ''}`.trim()}>
      {(title || aside) && (
        <header className="card-head">
          {title && <h2>{title}</h2>}
          {aside && <div className="aside">{aside}</div>}
        </header>
      )}
      {children}
    </section>
  )
}

/** Colour never carries meaning alone: every status has a word. */
export function Badge({ tone, children }: { tone: 'good' | 'warn' | 'bad' | 'info' | 'muted'; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>
}

export function Loading({ what }: { what?: string }) {
  return (
    <div className="state" role="status">
      Loading{what ? ` ${what}` : ''}…
    </div>
  )
}

export function ErrorBox({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : String(error)
  return (
    <div className="state error" role="alert">
      <strong>Something went wrong.</strong> {message}
    </div>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="state empty">{children}</div>
}

/** A tiny bar chart for a category's months. */
export function Spark({ values, label }: { values: number[]; label: string }) {
  const max = Math.max(1, ...values.map((v) => Math.abs(v)))
  return (
    <svg className="spark" viewBox={`0 0 ${values.length * 8} 24`} role="img" aria-label={label} preserveAspectRatio="none">
      {values.map((v, i) => {
        const h = Math.max(1, (Math.abs(v) / max) * 22)
        return <rect key={i} x={i * 8 + 1} y={24 - h} width={6} height={h} rx={1} />
      })}
    </svg>
  )
}

export const TYPE_COLORS: Record<string, string> = {
  in: 'var(--c-in)',
  bills: 'var(--c-bills)',
  required: 'var(--c-required)',
  extra: 'var(--c-extra)',
  oneTime: 'var(--c-onetime)',
  savings: 'var(--c-savings)',
  leftOver: 'var(--c-left)',
}

export function ShareBar({ parts }: { parts: { key: string; label: string; cents: number }[] }) {
  const total = parts.reduce((sum, p) => sum + Math.max(0, p.cents), 0)
  return (
    <div className="sharebar" role="img" aria-label={parts.map((p) => `${p.label} ${dollars(p.cents)}`).join(', ')}>
      {parts
        .filter((p) => p.cents > 0)
        .map((p) => (
          <span key={p.key} style={{ width: `${percent(p.cents, total)}%`, background: TYPE_COLORS[p.key] }} title={`${p.label}: ${dollars(p.cents)}`} />
        ))}
    </div>
  )
}
