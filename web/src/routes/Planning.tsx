import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { get, send } from '../api'
import { Badge, Card, Empty, ErrorBox, Loading } from '../components'
import { dateLabel, dollars, monthLabel, parseDollars, todayLocal } from '../format'
import { useMe } from '../auth'
import { STATUS_LABEL, STATUS_TONE, type PlanData } from '../phase4'
import { Panel, Problem, centsText, useAct } from './settings/common'

interface Named {
  id: number
  name: string
  kind: string
}

export function PlanCard({ plan, onChange }: { plan: PlanData; onChange?: () => void }) {
  const shown = plan.lifecycle === 'done' ? 'done' : plan.status
  return (
    <div className="plan-row">
      <div className="plan-head">
        <strong>{plan.name}</strong> <Badge tone={STATUS_TONE[shown]}>{STATUS_LABEL[shown]}</Badge>{' '}
        {!plan.isTrackable && <Badge tone="muted">not tracked</Badge>}
        {plan.lifecycle === 'cancelled' && <Badge tone="muted">cancelled</Badge>}
        {onChange && (
          <button type="button" className="btn" style={{ float: 'right' }} onClick={onChange}>
            Change
          </button>
        )}
      </div>
      <progress max={plan.targetCents} value={Math.min(Math.max(plan.actualCents, 0), plan.targetCents)} aria-label={`${plan.name}: ${dollars(plan.actualCents)} of ${dollars(plan.targetCents)}`} />
      <div>
        <strong>{dollars(plan.actualCents)}</strong> of {dollars(plan.targetCents)} by {dateLabel(plan.targetOn)}
        {plan.lifecycle === 'active' && (
          <span className="muted">
            {' '}
            · should be at {dollars(plan.expectedCents)} by now
          </span>
        )}
      </div>
      {plan.lifecycle === 'active' && (
        <div className="muted">
          {plan.monthsLeft > 0 ? (
            <>
              {dollars(plan.neededPerMonthCents)} a month for the {plan.monthsLeft} months left
              {plan.neededPerMonthWithInterestCents !== null && plan.rateBps ? `, or ${dollars(plan.neededPerMonthWithInterestCents)} counting the account's interest` : ''}.
            </>
          ) : (
            <>The date has come: {dollars(Math.max(0, plan.targetCents - plan.actualCents))} still to go.</>
          )}
          {plan.trouble && (
            <>
              {' '}
              {plan.trouble.status === 'behind' ? 'Behind' : 'Wavered'} in {monthLabel(plan.trouble.month)}.
            </>
          )}
        </div>
      )}
      <div className="muted">
        {plan.progressMode === 'account_balance' ? `Counted from the balance of ${plan.fundingAccountName ?? 'an account'}.` : `Counted from savings lines linked to it (${plan.linkedLines} so far). Link one from a line in the Ledger.`}
      </div>
    </div>
  )
}

export function Planning() {
  const me = useMe()
  const canEdit = me.user.role !== 'viewer'
  const plans = useQuery({ queryKey: ['plans'], queryFn: () => get<PlanData[]>('/plans', { includeCancelled: 'true' }) })
  const accounts = useQuery({ queryKey: ['accounts-list'], queryFn: () => get<Named[]>('/accounts') })
  const [editing, setEditing] = useState<PlanData | 'new' | null>(null)

  if (plans.isLoading || accounts.isLoading) return <Loading what="plans" />
  if (plans.error || !plans.data || !accounts.data) return <ErrorBox error={plans.error ?? accounts.error} />

  const active = plans.data.filter((p) => p.lifecycle === 'active')
  const finished = plans.data.filter((p) => p.lifecycle !== 'active')

  return (
    <>
      <h1>Planning</h1>
      {editing && canEdit && <PlanForm key={editing === 'new' ? 'new' : editing.id} plan={editing === 'new' ? null : editing} accounts={accounts.data} onClose={() => setEditing(null)} />}
      <Card
        title="Plans"
        aside={
          canEdit && (
            <button type="button" className="btn primary" onClick={() => setEditing('new')}>
              Make a plan
            </button>
          )
        }
      >
        <p className="muted">
          A plan is a target amount by a date. Tideline draws a straight line from where you started to the target and says whether you are on it. Plans that are tracked show on the
          Overview. To count money toward a plan without an account of its own, link savings lines to it from the <Link to="/lines">Ledger</Link>.
        </p>
        {active.length === 0 ? <Empty>No plans yet.</Empty> : active.map((p) => <PlanCard key={p.id} plan={p} onChange={canEdit ? () => setEditing(p) : undefined} />)}
      </Card>

      {finished.length > 0 && (
        <Card title="Finished and cancelled">
          {finished.map((p) => (
            <PlanCard key={p.id} plan={p} onChange={canEdit ? () => setEditing(p) : undefined} />
          ))}
        </Card>
      )}

      {active.some((p) => p.checkpoints.length > 1) && (
        <Card title="Month by month">
          {active
            .filter((p) => p.checkpoints.length > 1)
            .map((p) => (
              <div key={p.id} className="table-wrap">
                <table>
                  <caption>{p.name}</caption>
                  <thead>
                    <tr>
                      <th>Month end</th>
                      <th className="num">Should be</th>
                      <th className="num">Is</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {p.checkpoints.map((c) => (
                      <tr key={c.month}>
                        <td>{monthLabel(c.month)}</td>
                        <td className="num">{dollars(c.expectedCents)}</td>
                        <td className="num">{dollars(c.actualCents)}</td>
                        <td>
                          <Badge tone={STATUS_TONE[c.status]}>{STATUS_LABEL[c.status]}</Badge>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ))}
        </Card>
      )}
    </>
  )
}

function PlanForm({ plan, accounts, onClose }: { plan: PlanData | null; accounts: Named[]; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(plan?.name ?? '')
  const [target, setTarget] = useState(centsText(plan?.targetCents))
  const [startOn, setStartOn] = useState(plan?.startOn ?? todayLocal())
  const [targetOn, setTargetOn] = useState(plan?.targetOn ?? '')
  const [startAmount, setStartAmount] = useState(centsText(plan?.startAmountCents ?? 0))
  const [mode, setMode] = useState(plan?.progressMode ?? 'linked_lines')
  const [account, setAccount] = useState(plan?.fundingAccountId?.toString() ?? '')
  const [trackable, setTrackable] = useState(plan?.isTrackable ?? true)
  const [lifecycle, setLifecycle] = useState(plan?.lifecycle ?? 'active')
  const [notes, setNotes] = useState(plan?.notes ?? '')

  const targetCents = parseDollars(target)
  const startCents = startAmount.trim() === '' ? 0 : parseDollars(startAmount)
  const problem =
    name.trim() === ''
      ? 'Give the plan a name.'
      : targetCents === null || targetCents <= 0
        ? 'Enter the target as an amount more than zero.'
        : startCents === null
          ? 'The amount to start from is not a money amount.'
          : targetOn === ''
            ? 'Pick the date to be done by.'
            : targetOn <= startOn
              ? 'The target date has to be after the start date.'
              : mode === 'account_balance' && account === ''
                ? 'Pick the account whose balance this plan follows.'
                : null

  return (
    <Panel title={plan ? `Change ${plan.name}` : 'Make a plan'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (problem) return
          const body = {
            name: name.trim(), targetCents, startOn, targetOn, startAmountCents: startCents, progressMode: mode, fundingAccountId: account ? Number(account) : null,
            clearFunding: account === '', isTrackable: trackable, status: lifecycle, notes,
          }
          void run(() => (plan ? send('PATCH', `/plans/${plan.id}`, body) : send('POST', '/plans', body)))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Wood stove fund" />
        </label>
        <label>
          Target amount
          <input value={target} inputMode="decimal" onChange={(e) => setTarget(e.target.value)} />
        </label>
        <label>
          Start date
          <input type="date" value={startOn} onChange={(e) => setStartOn(e.target.value)} />
        </label>
        <label>
          Be done by
          <input type="date" value={targetOn} onChange={(e) => setTargetOn(e.target.value)} />
        </label>
        <label>
          Already put aside at the start
          <input value={startAmount} inputMode="decimal" onChange={(e) => setStartAmount(e.target.value)} />
        </label>
        <label>
          Progress is
          <select value={mode} onChange={(e) => setMode(e.target.value as PlanData['progressMode'])}>
            <option value="linked_lines">the savings lines I link to it</option>
            <option value="account_balance">the balance of an account</option>
          </select>
        </label>
        {mode === 'account_balance' && (
          <label>
            Account
            <select value={account} onChange={(e) => setAccount(e.target.value)}>
              <option value="">Pick an account</option>
              {accounts.filter((a) => ['checking', 'savings'].includes(a.kind)).map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label>
          <span>
            <input type="checkbox" checked={trackable} onChange={(e) => setTrackable(e.target.checked)} /> Show on the Overview and say whether it is on track
          </span>
        </label>
        {plan && (
          <label>
            Status
            <select value={lifecycle} onChange={(e) => setLifecycle(e.target.value as PlanData['lifecycle'])}>
              <option value="active">Active</option>
              <option value="done">Done</option>
              <option value="cancelled">Cancelled</option>
            </select>
          </label>
        )}
        <label>
          Notes
          <textarea value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || problem !== null}>
            Save
          </button>
          {problem && <span className="muted">{problem}</span>}
        </div>
      </form>
    </Panel>
  )
}
