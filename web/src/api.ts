// Typed client for /api/v1 (docs/07-api.md). Money is integer cents; dates are YYYY-MM-DD.

export interface Figure {
  cents: number
  drill: string | null
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message)
  }
}

export async function get<T>(path: string, params?: Record<string, string | undefined>): Promise<T> {
  const query = params
    ? '?' +
      Object.entries(params)
        .filter(([, v]) => v !== undefined && v !== '')
        .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v as string)}`)
        .join('&')
    : ''
  const response = await fetch(`/api/v1${path}${query}`)
  if (!response.ok) {
    let detail = response.statusText
    try {
      const problem = await response.json()
      detail = problem.detail ?? problem.title ?? (problem.errors ? Object.values(problem.errors).flat().join(' ') : detail)
    } catch {
      // not JSON
    }
    throw new ApiError(response.status, detail)
  }
  return (await response.json()) as T
}

/** Raw query string as built by a drill link, passed straight to /lines. */
export function getLines(query: string): Promise<LinePage> {
  return fetch(`/api/v1/lines?${query}`).then(async (r) => {
    if (!r.ok) {
      const problem = await r.json().catch(() => null)
      throw new ApiError(r.status, problem?.errors ? Object.values(problem.errors).flat().join(' ') : (problem?.title ?? r.statusText))
    }
    return (await r.json()) as LinePage
  })
}

// ---- lines -----------------------------------------------------------------------------------------------

export interface Line {
  transactionId: number
  splitLineId: number | null
  postedOn: string
  accountId: number
  accountName: string
  amountCents: number
  description: string
  payee: string | null
  categoryId: number | null
  categoryName: string | null
  type: string | null
  tags: string[]
  isSplitPart: boolean
  recurringId: number | null
  planId: number | null
  moveId: number | null
}

export interface LinePage {
  items: Line[]
  page: number
  pageSize: number
  total: number
  summary: { count: number; sumCents: number }
}

export interface TransactionDetail {
  id: number
  bank: {
    accountId: number
    accountName: string
    postedOn: string
    amountCents: number
    description: string
    bankBalanceCents: number | null
    checkNumber: string | null
    externalId: string
    source: string
  }
  raw: Record<string, string> | null
  payee: string | null
  categoryId: number | null
  categoryName: string | null
  type: string | null
  tags: string[]
  notes: string | null
  isManualOverride: boolean
  rule: { id: number; name: string } | null
  recurring: { id: number; name: string } | null
  planId: number | null
  moveId: number | null
  movePartner: { transactionId: number; accountName: string; postedOn: string; amountCents: number; description: string } | null
  splits: { id: number; amountCents: number; categoryId: number; categoryName: string; type: string; note: string | null; tags: string[] }[]
  unassignedCents: number
  version: string
  audit: { id: number; at: string; user: string | null; action: string; beforeJson: string | null; afterJson: string | null }[]
}

// ---- lookups ---------------------------------------------------------------------------------------------

export interface AccountInfo {
  id: number
  name: string
  kind: string
  institution: string | null
  bankNumber: string | null
}
export interface CategoryInfo {
  id: number
  name: string
  type: string
}

// ---- screens ---------------------------------------------------------------------------------------------

export interface Overview {
  from: string
  to: string
  spread: {
    in: Figure
    bills: Figure
    required: Figure
    extra: Figure
    oneTime: Figure
    savings: Figure
    out: Figure
    leftOver: Figure
  }
  months: { month: string; in: number; bills: number; required: number; extra: number; oneTime: number; savings: number; leftOver: number }[]
  plans: unknown[]
  flags: { alert: number; warn: number; info: number }
}

export interface AccountsByMonth {
  year: number
  accounts: {
    accountId: number
    name: string
    kind: string
    openingKnown: boolean
    cells: { month: string; inCents: number; outCents: number; netCents: number; endBalanceCents: number | null; drill: string }[]
  }[]
}

export interface Cards {
  month: string
  year: number
  cards: {
    accountId: number
    name: string
    balanceCents: number
    balanceDrill: string
    isPayInFull: boolean
    dueDay: number | null
    statementDay: number | null
    nextDue: string | null
    lastCheckedPaidInFull: boolean | null
    months: { month: string; purchasesCents: number; paymentsCents: number; endBalanceCents: number | null; paidInFull: boolean | null; drill: string }[]
    categories: { categoryId: number; name: string; cents: number; lines: number; drill: string }[]
    paymentsDrill: string
  }[]
}

export interface Bills {
  year: number
  rows: {
    recurringId: number | null
    name: string
    categoryName: string
    isOther: boolean
    cells: { month: string; status: string; paidCents: number; projectedCents: number; drill: string | null }[]
    totalCents: number
    totalProjectedCents: number
  }[]
  columnTotals: { month: string; paidCents: number; projectedCents: number }[]
  totalPaidCents: number
  totalProjectedCents: number
  grandTotalCents: number
  oneTimeAttention: { thresholdCents: number; drill: string; items: Line[] }
}

export interface TypeScreenData {
  type: string
  from: string
  to: string
  total: Figure
  categories: { id: number; name: string; cents: number; lines: number; drill: string; months: { month: string; cents: number }[] }[]
  payees: { payee: string; cents: number; lines: number; drill: string }[]
  subscriptions:
    | {
        id: number
        name: string
        category: string
        lastChargeCents: number | null
        previousChargeCents: number | null
        priceChanged: boolean
        monthlyCostCents: number
        yearlyCostCents: number
        nextCharge: string | null
        drill: string
      }[]
    | null
}

export interface Income {
  year: number
  total: Figure
  expectedCents: number | null
  months: { month: string; actualCents: number; expectedCents: number | null; drill: string }[]
  categories: { id: number; name: string; cents: number; lines: number; drill: string }[]
  payees: { payee: string; cents: number; lines: number; drill: string }[]
  reviewDeposits: { count: number; sumCents: number; drill: string; items: Line[] }
}
