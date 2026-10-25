import { parseDollars } from '../../format'
import { centsText } from './common'

export interface Named {
  id: number
  name: string
  kind?: string
}

export interface LoanTerms {
  loanType: string
  rate: string
  payment: string
  escrow: string
  day: string
  category: string
  secured: string
}

export interface RetirementTerms {
  accountType: string
  employee: string
  employer: string
  included: boolean
  pension: string
}

interface LoanStored {
  loanType: string
  rateBps: number | null
  paymentCents: number | null
  escrowCents: number | null
  paymentDay: number | null
  paymentCategoryId: number | null
  securedAssetId: number | null
}

interface RetirementStored {
  accountType: string
  employeeMonthlyCents: number
  employerMonthlyCents: number
  isIncludedInGoal: boolean
  pensionAnnualAtRetirementCents: number | null
}

export const LOAN_TYPES = ['mortgage', 'auto', 'student', 'personal', 'family', 'other']
export const RETIREMENT_TYPES = ['401k', '403b', '457', 'ira', 'roth_ira', 'pension', 'hsa', 'brokerage', 'other']

export const percentText = (bps: number | null | undefined) => (bps === null || bps === undefined ? '' : String(bps / 100))
export const bpsOf = (text: string) => (text.trim() === '' ? null : Math.round(Number(text) * 100))
export const validPercent = (t: string) => t.trim() === '' || (Number.isFinite(Number(t)) && Number(t) >= 0)
const validMoney = (t: string) => t.trim() === '' || parseDollars(t) !== null
const money = (t: string) => (t.trim() === '' ? null : parseDollars(t))

export function loanFrom(l: LoanStored | null | undefined): LoanTerms {
  return {
    loanType: l?.loanType ?? 'other',
    rate: percentText(l?.rateBps),
    payment: centsText(l?.paymentCents),
    escrow: centsText(l?.escrowCents),
    day: l?.paymentDay?.toString() ?? '',
    category: l?.paymentCategoryId?.toString() ?? '',
    secured: l?.securedAssetId?.toString() ?? '',
  }
}

export function retirementFrom(r: RetirementStored | null | undefined): RetirementTerms {
  return {
    accountType: r?.accountType ?? '401k',
    employee: centsText(r?.employeeMonthlyCents),
    employer: centsText(r?.employerMonthlyCents),
    included: r?.isIncludedInGoal ?? true,
    pension: centsText(r?.pensionAnnualAtRetirementCents),
  }
}

export type { LoanStored, RetirementStored }

export function loanProblem(l: LoanTerms): string | null {
  if (!validPercent(l.rate)) return 'The interest rate is not a percentage.'
  if (!validMoney(l.payment) || !validMoney(l.escrow)) return 'The payment is not a money amount.'
  return null
}

export function retirementProblem(r: RetirementTerms): string | null {
  return validMoney(r.employee) && validMoney(r.employer) && validMoney(r.pension) ? null : 'An amount is not a money amount.'
}

export const loanBody = (l: LoanTerms) => ({
  loanType: l.loanType,
  rateBps: bpsOf(l.rate),
  paymentCents: money(l.payment),
  escrowCents: money(l.escrow),
  paymentDay: l.day ? Number(l.day) : null,
  paymentCategoryId: l.category ? Number(l.category) : null,
  securedAssetId: l.secured ? Number(l.secured) : null,
})

export const retirementBody = (r: RetirementTerms) => ({
  accountType: r.accountType,
  employeeMonthlyCents: money(r.employee) ?? 0,
  employerMonthlyCents: money(r.employer) ?? 0,
  isIncludedInGoal: r.included,
  pensionAnnualAtRetirementCents: r.accountType === 'pension' ? money(r.pension) : null,
})

export function LoanFields({ value, onChange, categories, assets }: { value: LoanTerms; onChange: (v: LoanTerms) => void; categories: Named[]; assets: Named[] }) {
  const set = (patch: Partial<LoanTerms>) => onChange({ ...value, ...patch })
  return (
    <>
      <label>
        Kind of loan
        <select value={value.loanType} onChange={(e) => set({ loanType: e.target.value })}>
          {LOAN_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </label>
      <label>
        Interest rate, % a year
        <input value={value.rate} inputMode="decimal" onChange={(e) => set({ rate: e.target.value })} />
      </label>
      <label>
        Monthly payment, principal and interest only
        <input value={value.payment} inputMode="decimal" onChange={(e) => set({ payment: e.target.value })} />
      </label>
      <label>
        Escrow in the payment (taxes, insurance), not counted as principal
        <input value={value.escrow} inputMode="decimal" onChange={(e) => set({ escrow: e.target.value })} />
      </label>
      <label>
        Payment day of the month
        <input type="number" min={1} max={31} value={value.day} onChange={(e) => set({ day: e.target.value })} />
      </label>
      <label>
        Payments are lines in the category
        <select value={value.category} onChange={(e) => set({ category: e.target.value })}>
          <option value="">None: I will enter the balance myself</option>
          {categories.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
      </label>
      <label>
        Secured by (for equity)
        <select value={value.secured} onChange={(e) => set({ secured: e.target.value })}>
          <option value="">Nothing</option>
          {assets.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </select>
      </label>
    </>
  )
}

export function RetirementFields({ value, onChange }: { value: RetirementTerms; onChange: (v: RetirementTerms) => void }) {
  const set = (patch: Partial<RetirementTerms>) => onChange({ ...value, ...patch })
  return (
    <>
      <label>
        Kind of account
        <select value={value.accountType} onChange={(e) => set({ accountType: e.target.value })}>
          {RETIREMENT_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </label>
      <label>
        You put in each month
        <input value={value.employee} inputMode="decimal" onChange={(e) => set({ employee: e.target.value })} />
      </label>
      <label>
        Your employer puts in each month
        <input value={value.employer} inputMode="decimal" onChange={(e) => set({ employer: e.target.value })} />
      </label>
      {value.accountType === 'pension' && (
        <label>
          Pension a year at retirement
          <input value={value.pension} inputMode="decimal" onChange={(e) => set({ pension: e.target.value })} />
        </label>
      )}
      <label>
        <span>
          <input type="checkbox" checked={value.included} onChange={(e) => set({ included: e.target.checked })} /> Counted toward the retirement goal
        </span>
      </label>
    </>
  )
}
