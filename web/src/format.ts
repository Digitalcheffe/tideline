// Money is integer cents everywhere. UI copy: plain words, US dollars with cents, months as "Jan 2026".

export function dollars(cents: number): string {
  const negative = cents < 0
  const abs = Math.abs(Math.trunc(cents))
  const whole = Math.floor(abs / 100).toLocaleString('en-US')
  const frac = String(abs % 100).padStart(2, '0')
  return `${negative ? '-' : ''}$${whole}.${frac}`
}

/** A signed amount for a ledger line: income is plain, money out keeps its minus. */
export const signed = dollars

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

export function monthLabel(ym: string): string {
  const [y, m] = ym.split('-').map(Number)
  return `${MONTHS[m - 1]} ${y}`
}

export function shortMonth(ym: string): string {
  return MONTHS[Number(ym.split('-')[1]) - 1]
}

export function dateLabel(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number)
  return `${MONTHS[m - 1]} ${d}, ${y}`
}

export function pad(n: number): string {
  return String(n).padStart(2, '0')
}

export function iso(y: number, m: number, d: number): string {
  return `${y}-${pad(m)}-${pad(d)}`
}

export function lastDay(y: number, m: number): number {
  return new Date(y, m, 0).getDate()
}

export function monthRange(ym: string): { from: string; to: string } {
  const [y, m] = ym.split('-').map(Number)
  return { from: iso(y, m, 1), to: iso(y, m, lastDay(y, m)) }
}

export function addMonths(ym: string, delta: number): string {
  const [y, m] = ym.split('-').map(Number)
  const index = y * 12 + (m - 1) + delta
  return `${Math.floor(index / 12)}-${pad((index % 12) + 1)}`
}

export function currentMonth(now: Date = new Date()): string {
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}`
}

/** "2026-03-01".."2026-03-31" is a whole month. Returns "2026-03" or null. */
export function wholeMonth(from: string, to: string): string | null {
  const ym = from.slice(0, 7)
  const r = monthRange(ym)
  return r.from === from && r.to === to ? ym : null
}

export function wholeYear(from: string, to: string): number | null {
  const y = Number(from.slice(0, 4))
  return from === iso(y, 1, 1) && to === iso(y, 12, 31) ? y : null
}

export function percent(part: number, whole: number): number {
  return whole <= 0 ? 0 : Math.max(0, Math.min(100, (part / whole) * 100))
}

/** Plain-word names for the line types. */
export const TYPE_LABELS: Record<string, string> = {
  income: 'In',
  bill: 'Bills',
  required: 'Required',
  extra: 'Extra',
  one_time: 'One-time',
  savings: 'Savings',
}

export function typeLabel(type: string | null | undefined): string {
  return type ? (TYPE_LABELS[type] ?? type) : 'Move'
}

export function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`
}
