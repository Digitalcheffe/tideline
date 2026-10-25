import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CartesianGrid, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { get, send } from '../api'
import { Badge, Card, ErrorBox, Loading } from '../components'
import { dateLabel, dollars, parseDollars } from '../format'
import { useMe } from '../auth'
import { bpsText, type Assumptions, type RetirementAccount, type RetirementData, type RetirementPerson } from '../phase4'
import { SnapshotForm } from './NetWorth'
import { Panel, Problem, centsText, useAct } from './settings/common'

const ACCOUNT_TYPES = ['401k', '403b', '457', 'ira', 'roth_ira', 'pension', 'hsa', 'brokerage', 'other']

const years = (months: number) => (months / 12).toFixed(1)

function Big({ label, value, note, tone }: { label: string; value: string; note?: string; tone?: 'good' | 'bad' }) {
  return (
    <Card title={label}>
      <span className="big" style={tone ? { color: `var(--${tone})` } : undefined}>
        {value}
      </span>
      {note && <div className="muted">{note}</div>}
    </Card>
  )
}

export function Retirement() {
  const me = useMe()
  const canEdit = me.user.role !== 'viewer'
  const owner = me.user.role === 'owner'
  const data = useQuery({ queryKey: ['retirement'], queryFn: () => get<RetirementData>('/retirement') })
  const [person, setPerson] = useState<RetirementPerson | 'new' | null>(null)
  const [balance, setBalance] = useState<number | null>(null)
  const [account, setAccount] = useState<RetirementAccount | null>(null)
  const [assumptions, setAssumptions] = useState(false)

  if (data.isLoading) return <Loading what="retirement" />
  if (data.error || !data.data) return <ErrorBox error={data.error} />
  const r = data.data
  const res = r.result
  const a = r.assumptions

  return (
    <>
      <h1>Retirement</h1>
      <p className="banner" role="note">
        Projection, not advice. Change the assumptions to see how sensitive it is.
      </p>

      {!r.ready && (
        <Card title="To fill in">
          <p className="muted">There is not enough here to work out a number yet. Fill these in:</p>
          <ul>
            {r.missing.map((m) => (
              <li key={m}>{m}</li>
            ))}
          </ul>
        </Card>
      )}

      {res && (
        <>
          <div className="grid cols-4">
            <Big label="Required at retirement" value={dollars(res.requiredCents)} note={`to cover ${dollars(res.gapAtRetirementCents)} a year`} />
            <Big label="Projected" value={dollars(res.projectedCents)} note={`in ${years(res.monthsToRetirement)} years`} />
            <Big
              label={res.deficitCents > 0 ? 'Shortfall, in today’s dollars' : 'Surplus, in today’s dollars'}
              value={dollars(Math.abs(res.deficitTodayCents))}
              tone={res.deficitCents > 0 ? 'bad' : 'good'}
            />
            <Big
              label="Extra per month to close it"
              value={res.deficitCents > 0 ? dollars(res.extraMonthlyNeededCents) : dollars(0)}
              note={res.deficitCents > 0 ? 'on top of what goes in now' : 'nothing more needed'}
            />
          </div>

          <Card title="What it means">
            <ul>
              <li>
                {res.lastsPastPlan ? (
                  <>The money lasts past age {a.planToAge}.</>
                ) : (
                  <>
                    The money lasts about <strong>{res.moneyLastsYears?.toFixed(1)} years</strong> after retirement, short of age {a.planToAge} if the shortfall is not closed.
                  </>
                )}
              </li>
              <li>
                Retiring {r.retirementDate ? `on ${dateLabel(r.retirementDate)}` : ''}.{' '}
                {res.deficitCents > 0 ? (
                  r.retireAtAge !== null ? (
                    <>
                      If the last person to retire waits until age <strong>{r.retireAtAge}</strong>, with nothing else changed, projected savings cover what is required.
                    </>
                  ) : (
                    <>No retirement age up to 75 closes the gap with nothing else changed.</>
                  )
                ) : (
                  <>Projected savings already cover what is required.</>
                )}
              </li>
              <li>
                Inflation over the years to retirement multiplies today&rsquo;s prices by {res.inflationFactor.toFixed(3)}, so {dollars(res.gapTodayCents)} a year of gap today is{' '}
                {dollars(res.gapAtRetirementCents)} then.
              </li>
            </ul>
          </Card>

          <Card title="Projected balance by year">
            <div style={{ width: '100%', height: 260 }}>
              <ResponsiveContainer>
                <LineChart data={r.chart.map((p) => ({ ...p, label: `Year ${p.year}` }))} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                  <XAxis dataKey="label" stroke="var(--muted)" />
                  <YAxis stroke="var(--muted)" tickFormatter={(v: number) => `$${Math.round(v / 100).toLocaleString('en-US')}`} width={84} />
                  <Tooltip formatter={(v) => dollars(Number(v))} contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)' }} />
                  <ReferenceLine y={res.requiredCents} stroke="var(--bad)" strokeDasharray="6 4" label={{ value: 'Required', fill: 'var(--bad)', position: 'insideTopLeft' }} />
                  <Line dataKey="balanceCents" name="Projected" stroke="var(--accent)" strokeWidth={2} dot={false} />
                </LineChart>
              </ResponsiveContainer>
            </div>
          </Card>
        </>
      )}

      {/* ---- what it is worked out from ---- */}
      <h2>What this is worked out from</h2>

      {person && canEdit && <PersonForm key={person === 'new' ? 'new' : person.id} person={person === 'new' ? null : person} onClose={() => setPerson(null)} />}
      <Card
        title="People"
        aside={
          canEdit && (
            <button type="button" className="btn" onClick={() => setPerson('new')}>
              Add a person
            </button>
          )
        }
      >
        {r.people.length === 0 ? (
          <p className="muted">No one yet.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Born</th>
                  <th className="num">Retires at</th>
                  <th>On</th>
                  <th className="num">Social Security a year (today&rsquo;s dollars)</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {r.people.map((p) => (
                  <tr key={p.id}>
                    <td>
                      {p.name} {!p.isActive && <Badge tone="muted">left out</Badge>}
                    </td>
                    <td>{p.birthOn ? dateLabel(p.birthOn) : <span className="muted">not set</span>}</td>
                    <td className="num">{p.retireAge ?? ''}</td>
                    <td>{p.retirementDate ? dateLabel(p.retirementDate) : ''}</td>
                    <td className="num">{dollars(p.socialSecurityAnnualTodayCents)}</td>
                    <td>
                      {canEdit && (
                        <button type="button" className="btn" onClick={() => setPerson(p)}>
                          Change
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {assumptions && canEdit && <AssumptionsForm assumptions={a} onClose={() => setAssumptions(false)} />}
      <Card
        title="Goal and assumptions"
        aside={
          canEdit && (
            <button type="button" className="btn" onClick={() => setAssumptions(true)}>
              Change
            </button>
          )
        }
      >
        <dl className="kv">
          <dt>Income wanted in retirement, a year, today&rsquo;s dollars</dt>
          <dd>{a.goalAnnualIncomeTodayCents === null ? <span className="muted">not set</span> : dollars(a.goalAnnualIncomeTodayCents)}</dd>
          <dt>Other income, a year</dt>
          <dd>{dollars(a.otherIncomeAnnualTodayCents)}</dd>
          <dt>Return before retirement</dt>
          <dd>{bpsText(a.returnBeforeBps)}</dd>
          <dt>Return in retirement</dt>
          <dd>{bpsText(a.returnDuringBps)}</dd>
          <dt>Inflation</dt>
          <dd>{bpsText(a.inflationBps)}</dd>
          <dt>Withdrawal rate</dt>
          <dd>{bpsText(a.withdrawalRateBps)}</dd>
          <dt>Plan to age</dt>
          <dd>{a.planToAge}</dd>
        </dl>
      </Card>

      {balance !== null && owner && <SnapshotForm accounts={r.accounts.map((x) => ({ id: x.accountId, name: x.name, kind: 'retirement' }))} accountId={balance} onClose={() => setBalance(null)} />}
      {account && owner && <AccountTerms key={account.accountId} account={account} onClose={() => setAccount(null)} />}
      <Card title="Retirement accounts">
        {r.accounts.length === 0 ? (
          <p className="muted">No retirement accounts. Add one in Settings, Money accounts, with the kind Retirement.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Account</th>
                  <th className="num">Balance</th>
                  <th className="num">Put in each month</th>
                  <th>Counted</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {r.accounts.map((x) => (
                  <tr key={x.accountId}>
                    <td>
                      {x.name} <span className="muted">({x.accountType})</span>
                      {x.pensionAnnualAtRetirementCents !== null && <div className="muted">pension {dollars(x.pensionAnnualAtRetirementCents)} a year at retirement</div>}
                    </td>
                    <td className="num">
                      {x.balanceCents === null ? <span className="muted">not entered</span> : dollars(x.balanceCents)}
                      {x.asOf && (
                        <div className="muted">
                          as of {dateLabel(x.asOf)} {x.stale && <Badge tone="warn">stale</Badge>}
                        </div>
                      )}
                    </td>
                    <td className="num">{dollars(x.employeeMonthlyCents + x.employerMonthlyCents)}</td>
                    <td>{x.isIncluded ? 'Yes' : 'No'}</td>
                    <td>
                      {owner && (
                        <>
                          <button type="button" className="btn" onClick={() => setBalance(x.accountId)}>
                            Update balance
                          </button>{' '}
                          <button type="button" className="btn" onClick={() => setAccount(x)}>
                            Change
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {!owner && <p className="muted">An owner enters account balances and contributions.</p>}
      </Card>
    </>
  )
}

function PersonForm({ person, onClose }: { person: RetirementPerson | null; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [name, setName] = useState(person?.name ?? '')
  const [birth, setBirth] = useState(person?.birthOn ?? '')
  const [age, setAge] = useState(person?.retireAge?.toString() ?? '')
  const [ss, setSs] = useState(centsText(person?.socialSecurityAnnualTodayCents))
  const [active, setActive] = useState(person?.isActive ?? true)

  const ssCents = ss.trim() === '' ? 0 : parseDollars(ss)
  const valid = name.trim() !== '' && ssCents !== null

  return (
    <Panel title={person ? `Change ${person.name}` : 'Add a person'} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (!valid) return
          const body = { name: name.trim(), birthOn: birth || null, retireAge: age ? Number(age) : null, socialSecurityAnnualTodayCents: ssCents, isActive: active, clearBirth: person !== null && birth === '' }
          void run(() => (person ? send('PATCH', `/people/${person.id}`, body) : send('POST', '/people', body)))
        }}
      >
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Date of birth
          <input type="date" value={birth} onChange={(e) => setBirth(e.target.value)} />
        </label>
        <label>
          Age to retire
          <input type="number" min={30} max={100} value={age} onChange={(e) => setAge(e.target.value)} />
        </label>
        <label>
          Social Security a year, from the statement, in today&rsquo;s dollars
          <input value={ss} inputMode="decimal" onChange={(e) => setSs(e.target.value)} />
        </label>
        {person && (
          <label>
            <span>
              <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Counted in the plan
            </span>
          </label>
        )}
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || !valid}>
            Save
          </button>
        </div>
      </form>
    </Panel>
  )
}

function AssumptionsForm({ assumptions, onClose }: { assumptions: Assumptions; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [goal, setGoal] = useState(centsText(assumptions.goalAnnualIncomeTodayCents))
  const [other, setOther] = useState(centsText(assumptions.otherIncomeAnnualTodayCents))
  const pct = (bps: number) => String(bps / 100)
  const [before, setBefore] = useState(pct(assumptions.returnBeforeBps))
  const [during, setDuring] = useState(pct(assumptions.returnDuringBps))
  const [inflation, setInflation] = useState(pct(assumptions.inflationBps))
  const [withdrawal, setWithdrawal] = useState(pct(assumptions.withdrawalRateBps))
  const [planTo, setPlanTo] = useState(String(assumptions.planToAge))

  const goalCents = goal.trim() === '' ? null : parseDollars(goal)
  const otherCents = other.trim() === '' ? 0 : parseDollars(other)
  const bps = (t: string) => Math.round(Number(t) * 100)
  const valid = (goal.trim() === '' || goalCents !== null) && otherCents !== null && [before, during, inflation, withdrawal].every((t) => t.trim() !== '' && Number.isFinite(Number(t)))

  return (
    <Panel title="Goal and assumptions" onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (!valid) return
          void run(() =>
            send('PUT', '/retirement/goal', {
              goalAnnualIncomeTodayCents: goalCents,
              clearGoal: goalCents === null,
              otherIncomeAnnualTodayCents: otherCents,
              returnBeforeBps: bps(before),
              returnDuringBps: bps(during),
              inflationBps: bps(inflation),
              withdrawalRateBps: bps(withdrawal),
              planToAge: Number(planTo),
            }),
          )
        }}
      >
        <label>
          Income wanted in retirement, a year, today&rsquo;s dollars
          <input value={goal} inputMode="decimal" onChange={(e) => setGoal(e.target.value)} />
        </label>
        <label>
          Other income, a year (rent, part time work)
          <input value={other} inputMode="decimal" onChange={(e) => setOther(e.target.value)} />
        </label>
        <label>
          Return before retirement, % a year
          <input value={before} inputMode="decimal" onChange={(e) => setBefore(e.target.value)} />
        </label>
        <label>
          Return in retirement, % a year
          <input value={during} inputMode="decimal" onChange={(e) => setDuring(e.target.value)} />
        </label>
        <label>
          Inflation, % a year
          <input value={inflation} inputMode="decimal" onChange={(e) => setInflation(e.target.value)} />
        </label>
        <label>
          Withdrawal rate, % of savings a year
          <input value={withdrawal} inputMode="decimal" onChange={(e) => setWithdrawal(e.target.value)} />
        </label>
        <label>
          Plan to age
          <input type="number" min={60} max={120} value={planTo} onChange={(e) => setPlanTo(e.target.value)} />
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || !valid}>
            Save
          </button>
        </div>
      </form>
    </Panel>
  )
}

function AccountTerms({ account, onClose }: { account: RetirementAccount; onClose: () => void }) {
  const { run, error, busy } = useAct(onClose)
  const [type, setType] = useState(account.accountType)
  const [employee, setEmployee] = useState(centsText(account.employeeMonthlyCents))
  const [employer, setEmployer] = useState(centsText(account.employerMonthlyCents))
  const [included, setIncluded] = useState(account.isIncluded)
  const [pension, setPension] = useState(centsText(account.pensionAnnualAtRetirementCents))

  const e1 = employee.trim() === '' ? 0 : parseDollars(employee)
  const e2 = employer.trim() === '' ? 0 : parseDollars(employer)
  const pen = pension.trim() === '' ? null : parseDollars(pension)
  const valid = e1 !== null && e2 !== null && (pension.trim() === '' || pen !== null)

  return (
    <Panel title={`Change ${account.name}`} onClose={onClose}>
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault()
          if (valid)
            void run(() =>
              send('PATCH', `/accounts/${account.accountId}`, {
                retirement: { accountType: type, employeeMonthlyCents: e1, employerMonthlyCents: e2, isIncludedInGoal: included, pensionAnnualAtRetirementCents: pen },
              }),
            )
        }}
      >
        <label>
          Kind
          <select value={type} onChange={(e) => setType(e.target.value)}>
            {ACCOUNT_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
        </label>
        <label>
          You put in each month
          <input value={employee} inputMode="decimal" onChange={(e) => setEmployee(e.target.value)} />
        </label>
        <label>
          Your employer puts in each month
          <input value={employer} inputMode="decimal" onChange={(e) => setEmployer(e.target.value)} />
        </label>
        {type === 'pension' && (
          <label>
            Pension a year at retirement
            <input value={pension} inputMode="decimal" onChange={(e) => setPension(e.target.value)} />
          </label>
        )}
        <label>
          <span>
            <input type="checkbox" checked={included} onChange={(e) => setIncluded(e.target.checked)} /> Counted toward the goal
          </span>
        </label>
        <Problem message={error} />
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={busy || !valid}>
            Save
          </button>
        </div>
      </form>
    </Panel>
  )
}
