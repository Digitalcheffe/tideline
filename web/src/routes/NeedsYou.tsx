import { useState } from 'react'
import { Link } from 'react-router-dom'
import { send, type FlagItem, type Overview } from '../api'
import { Badge, Card } from '../components'
import { useMe } from '../auth'
import { Problem, useAct } from './settings/common'

const TONE = { alert: 'bad', warn: 'warn', info: 'info' } as const
const WORD = { alert: 'Alert', warn: 'Warning', info: 'Note' } as const

function Item({ flag, canAct }: { flag: FlagItem; canAct: boolean }) {
  const { run, error, busy } = useAct()
  const [asking, setAsking] = useState(false)
  const [note, setNote] = useState('')

  return (
    <li className="flag">
      <div>
        <Badge tone={TONE[flag.severity]}>{WORD[flag.severity]}</Badge> <strong>{flag.title}</strong>
      </div>
      {flag.detail && <div className="muted">{flag.detail}</div>}
      <div className="toolbar">
        <Link to={flag.link}>Look at it</Link>
        {canAct && !asking && (
          <button type="button" className="btn" onClick={() => setAsking(true)}>
            Yes, I know about it
          </button>
        )}
      </div>
      {asking && (
        <form
          className="toolbar"
          onSubmit={(e) => {
            e.preventDefault()
            void run(() => send('POST', `/flags/${flag.id}/acknowledge`, { note }))
          }}
        >
          <input aria-label="A note, if you like" placeholder="A note, if you like" value={note} maxLength={400} onChange={(e) => setNote(e.target.value)} />
          <button type="submit" className="btn primary" disabled={busy}>
            Acknowledge
          </button>
          <button type="button" className="btn" onClick={() => setAsking(false)}>
            Cancel
          </button>
        </form>
      )}
      <Problem message={error} />
    </li>
  )
}

/** What is open, most serious first. Acknowledging quiets a flag without hiding that it happened. */
export function NeedsYou({ data }: { data: Overview }) {
  const me = useMe()
  const canAct = me.user.role !== 'viewer'
  const total = data.flags.alert + data.flags.warn + data.flags.info

  return (
    <Card title="Needs you">
      {total === 0 ? (
        <p>
          <Badge tone="good">All clear</Badge> <span className="muted">Nothing needs your attention.</span>
        </p>
      ) : (
        <>
          <p>
            <Badge tone="bad">{data.flags.alert} alerts</Badge> <Badge tone="warn">{data.flags.warn} warnings</Badge> <Badge tone="info">{data.flags.info} notes</Badge>
          </p>
          <ul className="flags">
            {data.needsYou.map((f) => (
              <Item key={f.id} flag={f} canAct={canAct} />
            ))}
          </ul>
          {total > data.needsYou.length && <p className="muted">Showing the {data.needsYou.length} most serious of {total}.</p>}
        </>
      )}
    </Card>
  )
}
