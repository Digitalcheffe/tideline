import { useSearchParams } from 'react-router-dom'
import { addMonths, currentMonth, iso, monthRange, wholeMonth, wholeYear } from './format'

export interface Period {
  from: string
  to: string
  /** "month", "year" or "custom" (the picker shows the matching tab). */
  kind: 'month' | 'year' | 'custom'
  /** The year screens such as Bills and Income use. */
  year: number
  month: string
  setMonth: (ym: string) => void
  setYear: (year: number) => void
  setRange: (from: string, to: string) => void
}

/** The period lives in the URL (?from=&to=), so every screen respects it and any view can be shared as a link. */
export function usePeriod(): Period {
  const [params, setParams] = useSearchParams()
  const fallback = monthRange(currentMonth())
  const from = params.get('from') || fallback.from
  const to = params.get('to') || (params.get('from') ? from : fallback.to)

  const set = (f: string, t: string) => {
    const next = new URLSearchParams(params)
    next.set('from', f)
    next.set('to', t)
    setParams(next, { replace: true })
  }

  const month = wholeMonth(from, to)
  const year = wholeYear(from, to)
  return {
    from,
    to,
    kind: month ? 'month' : year ? 'year' : 'custom',
    year: Number(from.slice(0, 4)),
    month: from.slice(0, 7),
    setMonth: (ym) => set(monthRange(ym).from, monthRange(ym).to),
    setYear: (y) => set(iso(y, 1, 1), iso(y, 12, 31)),
    setRange: set,
  }
}

export function PeriodPicker({ period }: { period: Period }) {
  const { kind, month, year } = period
  return (
    <div className="period" role="group" aria-label="Period">
      <div className="seg">
        <button type="button" className={kind === 'month' ? 'on' : ''} onClick={() => period.setMonth(period.month)}>
          Month
        </button>
        <button type="button" className={kind === 'year' ? 'on' : ''} onClick={() => period.setYear(year)}>
          Year
        </button>
        <button type="button" className={kind === 'custom' ? 'on' : ''} onClick={() => period.setRange(period.from, period.to)}>
          Custom
        </button>
      </div>

      {kind === 'month' && (
        <div className="stepper">
          <button type="button" aria-label="Previous month" onClick={() => period.setMonth(addMonths(month, -1))}>
            ‹
          </button>
          <input type="month" aria-label="Month" value={month} onChange={(e) => e.target.value && period.setMonth(e.target.value)} />
          <button type="button" aria-label="Next month" onClick={() => period.setMonth(addMonths(month, 1))}>
            ›
          </button>
        </div>
      )}
      {kind === 'year' && (
        <div className="stepper">
          <button type="button" aria-label="Previous year" onClick={() => period.setYear(year - 1)}>
            ‹
          </button>
          <span className="year">{year}</span>
          <button type="button" aria-label="Next year" onClick={() => period.setYear(year + 1)}>
            ›
          </button>
        </div>
      )}
      {kind === 'custom' && (
        <div className="stepper">
          <input type="date" aria-label="From" value={period.from} onChange={(e) => e.target.value && period.setRange(e.target.value, period.to < e.target.value ? e.target.value : period.to)} />
          <span aria-hidden>to</span>
          <input type="date" aria-label="To" value={period.to} onChange={(e) => e.target.value && period.setRange(period.from > e.target.value ? e.target.value : period.from, e.target.value)} />
        </div>
      )}
    </div>
  )
}
