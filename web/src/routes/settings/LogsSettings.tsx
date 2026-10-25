import { Fragment, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get } from '../../api'
import { Badge, Card, Empty, ErrorBox, Loading } from '../../components'
import { when } from './common'

interface AuditItem {
  id: number
  at: string
  userId: number | null
  userName: string | null
  apiTokenName: string | null
  action: string
  entity: string
  entityId: string | null
  beforeJson: string | null
  afterJson: string | null
  ip: string | null
}

interface EventItem {
  id: number
  at: string
  level: string
  source: string
  message: string
  detailJson?: string | null
  detail?: string | null
}

interface Page<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

export function LogsSettings() {
  const [which, setWhich] = useState<'audit' | 'system'>('audit')
  return (
    <>
      <div className="toolbar">
        <div className="seg" role="group" aria-label="Which log">
          <button type="button" className={which === 'audit' ? 'on' : ''} onClick={() => setWhich('audit')}>
            Who changed what
          </button>
          <button type="button" className={which === 'system' ? 'on' : ''} onClick={() => setWhich('system')}>
            System events
          </button>
        </div>
      </div>
      {which === 'audit' ? <AuditLog /> : <SystemLog />}
    </>
  )
}

function csv(rows: string[][]): string {
  const cell = (v: string) => (/[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v)
  return rows.map((r) => r.map(cell).join(',')).join('\n')
}

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv' }))
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  URL.revokeObjectURL(url)
}

function AuditLog() {
  const [action, setAction] = useState('')
  const [entity, setEntity] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [page, setPage] = useState(1)
  const [open, setOpen] = useState<number | null>(null)

  const params = { action: action.trim(), entity: entity.trim(), from, to, page: String(page), pageSize: '50' }
  const log = useQuery({ queryKey: ['settings', 'audit', params], queryFn: () => get<Page<AuditItem>>('/audit', params) })
  const reset = <T,>(set: (v: T) => void) => (v: T) => {
    set(v)
    setPage(1)
  }

  return (
    <Card
      title="Who changed what"
      aside={
        <button
          type="button"
          className="btn"
          disabled={!log.data?.items.length}
          onClick={() =>
            download(
              'audit.csv',
              csv([
                ['when', 'who', 'action', 'entity', 'entity id', 'before', 'after'],
                ...(log.data?.items ?? []).map((a) => [a.at, a.userName ?? a.apiTokenName ?? 'system', a.action, a.entity, a.entityId ?? '', a.beforeJson ?? '', a.afterJson ?? '']),
              ]),
            )
          }
        >
          Download this page as CSV
        </button>
      }
    >
      <p className="muted">Every change is written here with what it was before and after, and cannot be edited or removed.</p>
      <div className="toolbar" style={{ flexWrap: 'wrap' }}>
        <input aria-label="Action" placeholder="Action, like rule.updated" value={action} onChange={(e) => reset(setAction)(e.target.value)} />
        <input aria-label="Kind of thing" placeholder="Kind of thing, like transaction" value={entity} onChange={(e) => reset(setEntity)(e.target.value)} />
        <input aria-label="From" type="date" value={from} onChange={(e) => reset(setFrom)(e.target.value)} />
        <input aria-label="To" type="date" value={to} onChange={(e) => reset(setTo)(e.target.value)} />
      </div>
      {log.isLoading ? (
        <Loading what="the log" />
      ) : log.error || !log.data ? (
        <ErrorBox error={log.error} />
      ) : log.data.items.length === 0 ? (
        <Empty>Nothing matches.</Empty>
      ) : (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>Who</th>
                  <th>What</th>
                  <th>About</th>
                </tr>
              </thead>
              <tbody>
                {log.data.items.map((a) => (
                  <Fragment key={a.id}>
                    <tr className="clickable" onClick={() => setOpen(open === a.id ? null : a.id)}>
                      <td>{when(a.at)}</td>
                      <td>{a.userName ?? (a.apiTokenName ? `token ${a.apiTokenName}` : 'the system')}</td>
                      <td>{a.action}</td>
                      <td>
                        {a.entity}
                        {a.entityId ? ` ${a.entityId}` : ''}
                      </td>
                    </tr>
                    {open === a.id && (
                      <tr>
                        <td colSpan={4}>
                          <dl className="kv">
                            <dt>Before</dt>
                            <dd>
                              <code>{a.beforeJson ?? 'nothing'}</code>
                            </dd>
                            <dt>After</dt>
                            <dd>
                              <code>{a.afterJson ?? 'nothing'}</code>
                            </dd>
                            {a.ip && (
                              <>
                                <dt>From</dt>
                                <dd>{a.ip}</dd>
                              </>
                            )}
                          </dl>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
          <Pager page={log.data.page} pageSize={log.data.pageSize} total={log.data.total} onPage={setPage} />
        </>
      )}
    </Card>
  )
}

function SystemLog() {
  const [level, setLevel] = useState('')
  const [source, setSource] = useState('')
  const [page, setPage] = useState(1)
  const params = { level, source: source.trim(), page: String(page), pageSize: '50' }
  const log = useQuery({ queryKey: ['settings', 'system', params], queryFn: () => get<Page<EventItem>>('/system-events', params) })

  return (
    <Card
      title="System events"
      aside={
        <button
          type="button"
          className="btn"
          disabled={!log.data?.items.length}
          onClick={() => download('system-events.csv', csv([['when', 'level', 'source', 'message'], ...(log.data?.items ?? []).map((e) => [e.at, e.level, e.source, e.message])]))}
        >
          Download this page as CSV
        </button>
      }
    >
      <p className="muted">What Tideline itself did: imports, balance checks, anything that went wrong.</p>
      <div className="toolbar">
        <select
          aria-label="Level"
          value={level}
          onChange={(e) => {
            setLevel(e.target.value)
            setPage(1)
          }}
        >
          <option value="">Every level</option>
          <option value="info">Information</option>
          <option value="warn">Warnings</option>
          <option value="error">Errors</option>
        </select>
        <input
          aria-label="Source"
          placeholder="Source, like import"
          value={source}
          onChange={(e) => {
            setSource(e.target.value)
            setPage(1)
          }}
        />
      </div>
      {log.isLoading ? (
        <Loading what="the log" />
      ) : log.error || !log.data ? (
        <ErrorBox error={log.error} />
      ) : log.data.items.length === 0 ? (
        <Empty>Nothing matches.</Empty>
      ) : (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>When</th>
                  <th>Level</th>
                  <th>Source</th>
                  <th>What happened</th>
                </tr>
              </thead>
              <tbody>
                {log.data.items.map((e) => (
                  <tr key={e.id}>
                    <td>{when(e.at)}</td>
                    <td>
                      <Badge tone={e.level === 'error' ? 'bad' : e.level === 'warn' ? 'warn' : 'info'}>{e.level}</Badge>
                    </td>
                    <td>{e.source}</td>
                    <td>{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pager page={log.data.page} pageSize={log.data.pageSize} total={log.data.total} onPage={setPage} />
        </>
      )}
    </Card>
  )
}

function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  const last = Math.max(1, Math.ceil(total / pageSize))
  return (
    <div className="pager">
      <button type="button" className="btn" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Newer
      </button>
      <span>
        Page {page} of {last} · {total} entries
      </span>
      <button type="button" className="btn" disabled={page >= last} onClick={() => onPage(page + 1)}>
        Older
      </button>
    </div>
  )
}
