// Types for the Phase 4 screens: net worth, retirement, plans, budget, savings. Money is integer cents.
import type { Figure } from './api'

export interface NwLine {
  accountId: number
  name: string
  kind: string
  cents: number
  asOf: string | null
  source: string
  stale: boolean
}

export interface Payoff {
  paysOff: boolean
  monthsLeft: number | null
  interestLeftCents: number
  payoffMonth: string | null
}

export interface LoanView {
  accountId: number
  name: string
  loanType: string
  balanceCents: number | null
  asOf: string | null
  source: string | null
  stale: boolean
  rateBps: number | null
  paymentCents: number | null
  escrowCents: number | null
  payoff: Payoff | null
  securedAssetId: number | null
  securedAssetName: string | null
  missingTerms: boolean
}

export interface NetWorthData {
  asOf: string
  assetsCents: number
  liabilitiesCents: number
  netWorthCents: number
  assets: NwLine[]
  liabilities: NwLine[]
  loans: LoanView[]
  equity: { assetId: number; assetName: string; assetCents: number; assetAsOf: string | null; assetStale: boolean; loanId: number; loanName: string; loanCents: number; equityCents: number }[]
  trend: { month: string; assetsCents: number; liabilitiesCents: number; netWorthCents: number }[]
  needsInput: string[]
}

export interface Countdown {
  extraCents: number
  without: Payoff
  with: Payoff
  monthsSaved: number | null
  interestSavedCents: number
}

export interface RetirementPerson {
  id: number
  name: string
  birthOn: string | null
  retireAge: number | null
  socialSecurityAnnualTodayCents: number
  isActive: boolean
  retirementDate: string | null
}

export interface RetirementAccount {
  accountId: number
  name: string
  accountType: string
  balanceCents: number | null
  asOf: string | null
  stale: boolean
  employeeMonthlyCents: number
  employerMonthlyCents: number
  isIncluded: boolean
  pensionAnnualAtRetirementCents: number | null
}

export interface Assumptions {
  goalAnnualIncomeTodayCents: number | null
  otherIncomeAnnualTodayCents: number
  returnBeforeBps: number
  returnDuringBps: number
  inflationBps: number
  withdrawalRateBps: number
  planToAge: number
}

export interface RetirementResult {
  monthsToRetirement: number
  projectedCents: number
  inflationFactor: number
  gapTodayCents: number
  gapAtRetirementCents: number
  requiredCents: number
  deficitCents: number
  deficitTodayCents: number
  extraMonthlyNeededCents: number
  moneyLastsYears: number | null
  lastsPastPlan: boolean
}

export interface RetirementData {
  ready: boolean
  missing: string[]
  people: RetirementPerson[]
  accounts: RetirementAccount[]
  assumptions: Assumptions
  retirementDate: string | null
  monthsToRetirement: number | null
  result: RetirementResult | null
  retireAtAge: number | null
  chart: { year: number; balanceCents: number }[]
  socialSecurityTotalAnnualTodayCents: number
}

export interface PlanData {
  id: number
  name: string
  targetCents: number
  startOn: string
  targetOn: string
  startAmountCents: number
  fundingAccountId: number | null
  fundingAccountName: string | null
  progressMode: 'account_balance' | 'linked_lines'
  isTrackable: boolean
  lifecycle: 'active' | 'done' | 'cancelled'
  notes: string | null
  actualCents: number
  expectedCents: number
  status: 'on_track' | 'wavered' | 'behind' | 'done'
  monthsLeft: number
  neededPerMonthCents: number
  neededPerMonthWithInterestCents: number | null
  rateBps: number | null
  trouble: { month: string; status: string } | null
  checkpoints: { month: string; expectedCents: number; actualCents: number; status: string }[]
  linkedLines: number
}

export interface BudgetData {
  year: number
  month: string
  elapsedMonths: number
  popPercent: number
  popMinCents: number
  saved: boolean
  income: { expectedAnnualCents: number | null; expectedYtdCents: number | null; short: boolean; actualYtd: Figure }
  categories: { categoryId: number; name: string; type: string; monthlyCents: number | null; annualCents: number | null; popped: boolean; thisMonth: Figure; ytd: Figure }[]
}

export interface BudgetProposals {
  from: string
  to: string
  items: { categoryId: number; name: string; monthlyCents: number; twelveMonthCents: number; currentMonthlyCents: number | null; currentAnnualCents: number | null }[]
}

export interface SavingsData {
  totalBalanceCents: number
  totalAverageMonthlyDepositCents: number
  accounts: {
    accountId: number
    name: string
    balanceCents: number
    rateBps: number | null
    averageMonthlyDepositCents: number
    interestPerMonthCents: number
    deposits: { month: string; cents: number; drill: string | null }[]
  }[]
  goals: { plan: PlanData; projectedAtTargetCents: number | null; onPace: boolean | null }[]
}

export const STATUS_LABEL: Record<string, string> = {
  on_track: 'On track',
  wavered: 'Wavered',
  behind: 'Behind',
  done: 'Done',
}

export const STATUS_TONE: Record<string, 'good' | 'warn' | 'bad' | 'info'> = {
  on_track: 'good',
  wavered: 'warn',
  behind: 'bad',
  done: 'info',
}

export const bpsText = (bps: number) => `${(bps / 100).toFixed(bps % 100 === 0 ? 0 : 2)}%`
