import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { dollars, parseDollars } from '../../format'
import { Panel, Problem, centsText, useAct } from './common'
import {
  LoanFields, RetirementFields, bpsOf, loanBody, loanFrom, loanProblem, percentText, retirementBody, retirementFrom, retirementProblem, validPercent,
  type LoanStored, type LoanTerms, type RetirementStored, type RetirementTerms,
} from './TermsFields'

interface AccountRow {
  id: number
  name: string
  kind: string
  institution: string | null
  bankNumberMask: string | null
  openingBalanceCents: number | null
  openingOn: string | null
  isInSpread: boolean
  isClosed: boolean
  csvUntilOn: string | null
  notes: string | null
  lineCount: number
  interestRateBps: number | null
  loan: LoanStored | null
  retirement: RetirementStored | null
  card: { isPayInFull: boolean; statementDay: number | null; dueDay: number | null; creditLimitCents: number | null } | null
}

interface PatternRow {
  id: number
  fromAccountId: number
  fromAccountName: string
  fromContains: string
  toAccountId: number
  toAccountName: string
  toContains: string
  maxDaysApart: number
}

const KINDS: [string, string][] = [
  ['checking', 'Checking'],
  ['savings', 'Savings'],
  ['credit_card', 'Credit card'],
  ['loan', 'Loan'],
  ['asset', 'Asset'],
  ['retirement', 'Retirement'],
]
const kindLabel = (k: string) => KINDS.find(([v]) => v === k)?.[1] ?? k

export function AccountsSettings() {
  const accounts = useQuery({ queryKey: ['settings', 'accounts'], queryFn: () => get<AccountRow[]>('/accounts') })
  const patterns = useQuery({ queryKey: ['settings', 'patterns'], queryFn: () => get<PatternRow[]>('/move-patterns') })
  const [editing, setEditing] = useState<AccountRow | 'new' | null>(null)
  const [pattern, setPattern] = useState<PatternRow | 'new' | null>(null)

  if (accounts.isLoading || patterns.isLoading) return <Loading what="accounts" />
  if (accounts.error || patterns.error || !accounts.data || !patterns.data) return <ErrorBox error={accounts.error ?? patterns.error} />

  return (
    <>
      {editing && <AccountForm key={editing === 'new' ? 'new' : editing.id} account={editing === 'new' ? null : editing} all={accounts.data} onClose={() => setEditing(null)} />}
      <Card
        title="Money accounts"
        aside={
          <button type="button" className="btn primary" onClick={() => setEditing('new')}>
            Add an account
          </button>
        }
      >
        <p className="muted">
          Bank accounts and cards. The numbers on the bank's own lines never change here; this is only how Tideline treats the account.
        </p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Kind</th>
                <th>Number</th>
                <th>Opening balance</th>
                <th>CSV until</th>
                <th>Card terms</th>
                <th className="num">Lines</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {accounts.data.map((a) => (
                <tr key={a.id}>
                  <td>
                    {a.name} {a.isClosed && <Badge tone="muted">closed</Badge>} {!a.isInSpread && <Badge tone="info">not in the spread</Badge>}
                  </td>
                  <td>{kindLabel(a.kind)}</td>
                  <td>{a.bankNumberMask ?? ''}</td>
                  <td>{a.openingBalanceCents === null ? <span className="muted">not set</span> : `${dollars(a.openingBalanceCents)} on ${a.openingOn}`}</td>
                  <td>{a.csvUntilOn ?? ''}</td>
                  <td>
                    {a.card
                      ? `${a.card.isPayInFull ? 'Pay in full' : 'Carries a balance'}${a.card.dueDay ? `, due day ${a.card.dueDay}` : ''}${a.card.statementDay ? `, statement day ${a.card.statementDay}` : ''}`
                      : ''}
                  </td>
                  <td className="num">{a.lineCount}</td>
                  <td>
                    <button type="button" className="btn" onClick={() => setEditing(a)}>
                      Change
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {pattern && <PatternForm key={pattern === 'new' ? 'new' : pattern.id} pattern={pattern === 'new' ? null : pattern} accounts={accounts.data} onClose={() => setPattern(null)} />}
      <Card
        title="Transfers between your accounts"
        aside={
          <button type="button" className="btn primary" onClick={() => setPattern('new')}>
            Add a pattern
          </button>
        }
      >
        <p className="muted">
          When a line leaves one account and a line with the matching words arrives in another within a few days, they are paired as a move and
          left out of the spread.
        </p>
        {patterns.data.length === 0 ? (
          <p className="muted">No patterns yet.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Leaves</th>
                  <th>Words on that line</th>
                  <th>Arrives in</th>
                  <th>Words on that line</th>
                  <th className="num">Days apart</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {patterns.data.map((p) => (
                  <tr key={p.id}>
                    <td>{p.fromAccountName}</td>
                    <td>{p.fromContains}</td>
                    <td>{p.toAccountName}</td>
                    <td>{p.toContains}</td>
                    <td className="num">{p.maxDaysApart}</td>
                    <td>
                      <button type="button" className="btn" onClick={() => setPattern(p)}>
                        Change
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  )
}

function AccountForm({ account, all, onClose }: { account: AccountRow | null; all: AccountRow[]; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const categories = useQuery({ queryKey: ['categories'], queryFn: () => get<{ id: number; name: string }[]>('/categories') })
  const [loan, setLoan] = useState<LoanTerms>(() => loanFrom(account?.loan))
  const [retire, setRetire] = useState<RetirementTerms>(() => retirementFrom(account?.retirement))
  const [rate, setRate] = useState(percentText(account?.interestRateBps))
  const [name, setName] = useState(account?.name ?? '')
  const [kind, setKind] = useState(account?.kind ?? 'checking')
  const [institution, setInstitution] = useState(account?.institution ?? '')
  const [bankNumber, setBankNumber] = useState('')
  const [opening, setOpening] = useState(centsText(account?.openingBalanceCents))
  const [openingOn, setOpeningOn] = useState(account?.openingOn ?? '')
  const [csvUntil, setCsvUntil] = useState(account?.csvUntilOn ?? '')
  const [inSpread, setInSpread] = useState(account?.isInSpread ?? true)
  const [closed, setClosed] = useState(account?.isClosed ?? false)
  const [notes, setNotes] = useState(account?.notes ?? '')
  const [payInFull, setPayInFull] = useState(account?.card?.isPayInFull ?? true)
  const [statementDay, setStatementDay] = useState(account?.card?.statementDay?.toString() ?? '')
  const [dueDay, setDueDay] = useState(account?.card?.dueDay?.toString() ?? '')
  const [limit, setLimit] = useState(centsText(account?.card?.creditLimitCents))

  const openingCents = opening.trim() === '' ? null : parseDollars(opening)
  const limitCents = limit.trim() === '' ? null : parseDollars(limit)
  const problem =
    name.trim() === ''
      ? 'Give the account a name.'
      : openingCents === null && opening.trim() !== ''
        ? 'The opening balance is not a money amount.'
        : openingCents !== null && openingOn === ''
          ? 'Say which day the opening balance was true.'
          : limitCents === null && limit.trim() !== ''
            ? 'The credit limit is not a money amount.'
            : kind === 'loan'
              ? loanProblem(loan)
              : kind === 'retirement'
                ? retirementProblem(retire)
                : kind === 'savings' && !validPercent(rate)
                  ? 'The interest rate is not a percentage.'
                  : null

  const save = () => {
    const card =
      kind === 'credit_card'
        ? {
            isPayInFull: payInFull,
            statementDay: statementDay ? Number(statementDay) : null,
            dueDay: dueDay ? Number(dueDay) : null,
            creditLimitCents: limitCents,
          }
        : undefined
    const body = {
      name: name.trim(),
      kind,
      institution: institution.trim() || null,
      // left empty on a change means keep what is there; only the last 4 digits are ever sent or kept
      bankNumber: bankNumber === '' ? undefined : bankNumber,
      openingBalanceCents: openingCents,
      openingOn: openingCents === null ? null : openingOn,
      clearOpening: account !== null && openingCents === null && account.openingBalanceCents !== null,
      csvUntilOn: csvUntil || (account ? '' : null),
      isInSpread: inSpread,
      isClosed: closed,
      notes: notes.trim() || null,
      card,
      loan: kind === 'loan' ? loanBody(loan) : undefined,
      retirement: kind === 'retirement' ? retirementBody(retire) : undefined,
      interestRateBps: kind === 'savings' ? bpsOf(rate) : undefined,
      clearRate: kind === 'savings' && rate.trim() === '' && account?.interestRateBps != null,
    }
    return run(() => (account ? send('PATCH', `/accounts/${account.id}`, body) : send('POST', '/accounts', body)))
  }

  return (
    <Panel title={account ? `Change ${account.name}` : 'Add an account'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (!problem) void save()
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Kind
          <select value={kind} onChange={(e) => setKind(e.target.value)} disabled={account !== null}>
            {KINDS.map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
        </label>
        <label>
          Bank
          <input value={institution} onChange={(e) => setInstitution(e.target.value)} />
        </label>
        <label>
          Last 4 digits of the account number
          <input
            value={bankNumber}
            inputMode="numeric"
            maxLength={4}
            placeholder={account?.bankNumberMask ? `${account.bankNumberMask} (leave empty to keep it)` : '1234'}
            onChange={(e) => setBankNumber(e.target.value.replace(/\D/g, '').slice(-4))}
          />
          <span className="muted">Only these 4 digits are kept, and not as digits: Tideline stores a hash to match files and a sealed copy to show you the mask. Type or paste a longer number and the rest is thrown away.</span>
        </label>
        <label>
          Opening balance (leave empty if you do not know it)
          <input value={opening} inputMode="decimal" onChange={(e) => setOpening(e.target.value)} />
        </label>
        <label>
          Opening balance was true on
          <input type="date" value={openingOn} onChange={(e) => setOpeningOn(e.target.value)} />
        </label>
        <label>
          CSV files stop on (when the automatic feed takes over)
          <input type="date" value={csvUntil} onChange={(e) => setCsvUntil(e.target.value)} />
        </label>
        {kind === 'credit_card' && (
          <>
            <label>
              <span>
                <input type="checkbox" checked={payInFull} onChange={(e) => setPayInFull(e.target.checked)} /> Paid in full every month
              </span>
            </label>
            <label>
              Statement day of the month
              <input type="number" min={1} max={31} value={statementDay} onChange={(e) => setStatementDay(e.target.value)} />
            </label>
            <label>
              Payment due day of the month
              <input type="number" min={1} max={31} value={dueDay} onChange={(e) => setDueDay(e.target.value)} />
            </label>
            <label>
              Credit limit
              <input value={limit} inputMode="decimal" onChange={(e) => setLimit(e.target.value)} />
            </label>
          </>
        )}
        {kind === 'savings' && (
          <label>
            Interest it pays, % a year (used for the Savings projections)
            <input value={rate} inputMode="decimal" onChange={(e) => setRate(e.target.value)} />
          </label>
        )}
        {kind === 'loan' && (
          <LoanFields
            value={loan}
            onChange={setLoan}
            categories={categories.data ?? []}
            assets={all.filter((a) => a.kind === 'asset' && a.id !== account?.id)}
          />
        )}
        {kind === 'retirement' && <RetirementFields value={retire} onChange={setRetire} />}
        <label>
          <span>
            <input type="checkbox" checked={inSpread} onChange={(e) => setInSpread(e.target.checked)} /> Counts in the spread
          </span>
        </label>
        {account && (
          <label>
            <span>
              <input type="checkbox" checked={closed} onChange={(e) => setClosed(e.target.checked)} /> Closed
            </span>
          </label>
        )}
        <label>
          Notes
          <textarea value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || problem !== null} title={problem ?? undefined}>
            Save
          </button>
          {problem && <span className="muted">{problem}</span>}
        </div>
      </form>
    </Panel>
  )
}

function PatternForm({ pattern, accounts, onClose }: { pattern: PatternRow | null; accounts: AccountRow[]; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [from, setFrom] = useState(pattern?.fromAccountId?.toString() ?? '')
  const [fromContains, setFromContains] = useState(pattern?.fromContains ?? '')
  const [to, setTo] = useState(pattern?.toAccountId?.toString() ?? '')
  const [toContains, setToContains] = useState(pattern?.toContains ?? '')
  const [days, setDays] = useState(String(pattern?.maxDaysApart ?? 3))

  const valid = from !== '' && to !== '' && from !== to && fromContains.trim() !== '' && toContains.trim() !== ''
  const body = () => ({
    fromAccountId: Number(from),
    fromContains: fromContains.trim(),
    toAccountId: Number(to),
    toContains: toContains.trim(),
    maxDaysApart: Number(days) || 3,
  })

  return (
    <Panel title={pattern ? 'Change this pattern' : 'Add a transfer pattern'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (valid) void run(() => (pattern ? send('PATCH', `/move-patterns/${pattern.id}`, body()) : send('POST', '/move-patterns', body())))
        }}
      >
        <label>
          Money leaves
          <select value={from} onChange={(e) => setFrom(e.target.value)}>
            <option value="">Pick an account</option>
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Words on that line
          <input value={fromContains} onChange={(e) => setFromContains(e.target.value)} placeholder="TRANSFER TO" />
        </label>
        <label>
          Money arrives in
          <select value={to} onChange={(e) => setTo(e.target.value)}>
            <option value="">Pick an account</option>
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Words on that line
          <input value={toContains} onChange={(e) => setToContains(e.target.value)} placeholder="TRANSFER FROM" />
        </label>
        <label>
          Most days apart
          <input type="number" min={0} max={30} value={days} onChange={(e) => setDays(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || !valid}>
            Save
          </button>
          {pattern && (
            <button
              type="button"
              className="btn"
              disabled={busy}
              onClick={() => {
                if (window.confirm('Remove this pattern? Moves already paired stay paired.')) void run(() => send('DELETE', `/move-patterns/${pattern.id}`))
              }}
            >
              Remove
            </button>
          )}
        </div>
      </form>
    </Panel>
  )
}
