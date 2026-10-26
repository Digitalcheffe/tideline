import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get, send, type TransactionDetail } from '../api'
import { Badge, ErrorBox, Loading, Money } from '../components'
import { dateLabel, typeLabel } from '../format'
import { EditPanel } from './EditPanel'

/** The transaction drawer (docs/ARCHITECTURE.md). Editing arrives with the splits editor. */
export function TransactionDrawer({ id, onClose }: { id: number; onClose: () => void }) {
  const { data, error, isLoading } = useQuery({ queryKey: ['transaction', id], queryFn: () => get<TransactionDetail>(`/transactions/${id}`) })

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <>
      <div className="scrim" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-label="Transaction">
        <div className="toolbar" style={{ justifyContent: 'space-between' }}>
          <strong>Transaction</strong>
          <button className="btn" onClick={onClose}>
            Close
          </button>
        </div>

        {isLoading && <Loading what="the transaction" />}
        {error && <ErrorBox error={error} />}
        {data && <Detail key={data.version} t={data} />}
      </aside>
    </>
  )
}

/** A line a person took out of the totals: why, and the way back. */
function LeftOutNotice({ t }: { t: TransactionDetail }) {
  const client = useQueryClient()
  const back = useMutation({
    mutationFn: () => send('POST', '/transactions/bring-back', { ids: [t.id] }),
    onSuccess: () => client.invalidateQueries(),
  })
  return (
    <div className="leftout-note" role="status">
      <Badge tone="warn">Left out of the totals</Badge> Since {dateLabel(t.leftOut!.at.slice(0, 10))}
      {t.leftOut!.reason ? `: ${t.leftOut!.reason}` : '.'} It is in no total, count or alert. Nothing about it has changed.
      <p>
        <button className="btn primary" type="button" disabled={back.isPending} onClick={() => back.mutate()}>
          {back.isPending ? 'Working…' : 'Bring it back'}
        </button>
        {back.error && (
          <span className="problem" role="alert">
            {' '}
            {back.error.message}
          </span>
        )}
      </p>
    </div>
  )
}

/**
 * Taking a line out. A bank line is never deleted, so it is left out of the totals (and can be brought back); a line added by
 * hand can be deleted outright.
 */
function RemoveLine({ t }: { t: TransactionDetail }) {
  const client = useQueryClient()
  const [reason, setReason] = useState('')
  const leaveOut = useMutation({
    mutationFn: () => send('POST', '/transactions/leave-out', { ids: [t.id], reason: reason.trim() || undefined }),
    onSuccess: () => client.invalidateQueries(),
  })
  const remove = useMutation({
    mutationFn: () => send('DELETE', `/transactions/${t.id}`),
    onSuccess: () => client.invalidateQueries(),
  })
  const error = leaveOut.error ?? remove.error
  const manual = t.bank.source === 'manual'

  return (
    <>
      <h3>Remove this line</h3>
      <p className="muted">
        {manual
          ? 'You added this line by hand, so you can delete it, or just leave it out of the totals.'
          : 'This came from the bank, so it is never deleted. For a duplicate or a line that should not count, leave it out of the totals. You can bring it back at any time.'}
      </p>
      <div className="toolbar">
        <input type="text" placeholder="Why? (optional)" aria-label="Why leave this line out" maxLength={200} value={reason} onChange={(e) => setReason(e.target.value)} />
        <button className="btn" type="button" disabled={leaveOut.isPending} onClick={() => leaveOut.mutate()}>
          {leaveOut.isPending ? 'Working…' : 'Leave out of the totals'}
        </button>
        {manual && (
          <button
            className="btn"
            type="button"
            disabled={remove.isPending}
            onClick={() => {
              if (window.confirm('Delete this line for good? A record of it stays in the history log.')) remove.mutate()
            }}
          >
            {remove.isPending ? 'Deleting…' : 'Delete this line'}
          </button>
        )}
      </div>
      {error && (
        <p className="problem" role="alert">
          {error.message}
        </p>
      )}
    </>
  )
}

function Detail({ t }: { t: TransactionDetail }) {
  const client = useQueryClient()
  const unpair = useMutation({
    mutationFn: () => send('DELETE', `/moves/${t.moveId}`),
    onSuccess: () => client.invalidateQueries(),
  })
  const unpairButton = (label: string) => (
    <p>
      <button className="btn" type="button" disabled={unpair.isPending} onClick={() => unpair.mutate()}>
        {unpair.isPending ? 'Working…' : label}
      </button>
      {unpair.error && (
        <span className="problem" role="alert">
          {' '}
          {unpair.error.message}
        </span>
      )}
    </p>
  )

  return (
    <>
      {t.leftOut && <LeftOutNotice t={t} />}
      <div className="frombank">
        <strong>From the bank</strong> <span className="muted">(never changes)</span>
        <dl className="kv" style={{ marginTop: 6 }}>
          <dt>Account</dt>
          <dd>{t.bank.accountName}</dd>
          <dt>Posted</dt>
          <dd>{dateLabel(t.bank.postedOn)}</dd>
          <dt>Amount</dt>
          <dd>
            <Money cents={t.bank.amountCents} />
          </dd>
          <dt>Description</dt>
          <dd>{t.bank.description}</dd>
          {t.bank.checkNumber && (
            <>
              <dt>Check number</dt>
              <dd>{t.bank.checkNumber}</dd>
            </>
          )}
          {t.bank.bankBalanceCents !== null && (
            <>
              <dt>Bank balance that day</dt>
              <dd>
                <Money cents={t.bank.bankBalanceCents} />
              </dd>
            </>
          )}
          <dt>Source</dt>
          <dd>
            {t.bank.source} <span className="muted">({t.bank.externalId})</span>
          </dd>
        </dl>
      </div>

      <h3>Meaning</h3>
      {t.movePartner ? (
        <>
          <p>
            <Badge tone="muted">Transfer</Badge> The other side is <strong>{t.movePartner.accountName}</strong> on {dateLabel(t.movePartner.postedOn)}:{' '}
            <Money cents={t.movePartner.amountCents} /> “{t.movePartner.description}”. Transfers between your own accounts are not counted as money in or out.
          </p>
          {unpairButton('Not a transfer: unpair')}
        </>
      ) : t.moveId ? (
        <>
          <p>
            <Badge tone="warn">Waiting for its other side</Badge> This looks like a transfer. It pairs when the other account's export is imported.
            If it is not a transfer, release it and the rules will treat it as an ordinary line.
          </p>
          {unpairButton('Release this line')}
        </>
      ) : (
        <dl className="kv">
          <dt>Type</dt>
          <dd>{typeLabel(t.type)}</dd>
          <dt>Category</dt>
          <dd>{t.categoryName ?? 'No category'}</dd>
          <dt>Payee</dt>
          <dd>{t.payee ?? '–'}</dd>
          <dt>Tags</dt>
          <dd>{t.tags.length ? t.tags.map((x) => <Badge key={x} tone={x === 'review' ? 'warn' : 'muted'}>{x}</Badge>) : '–'}</dd>
          <dt>Notes</dt>
          <dd>{t.notes ?? '–'}</dd>
          {t.recurring && (
            <>
              <dt>Recurring</dt>
              <dd>{t.recurring.name}</dd>
            </>
          )}
          {t.asset && (
            <>
              <dt>Asset</dt>
              <dd>
                <Link to="/assets">{t.asset.name}</Link>
              </dd>
            </>
          )}
          <dt>Set by</dt>
          <dd>
            {t.isManualOverride ? (
              <Badge tone="info">You, by hand</Badge>
            ) : t.rule ? (
              <>rule <strong>{t.rule.name}</strong></>
            ) : (
              <span className="muted">no rule matched, so it went to the catch-all</span>
            )}
          </dd>
        </dl>
      )}

      {!t.moveId && !t.leftOut && <EditPanel t={t} />}
      {!t.moveId && !t.leftOut && <RemoveLine t={t} />}

      <h3>Splits as they stand</h3>
      {t.splits.length === 0 ? (
        <p className="muted">Not split.</p>
      ) : (
        <table>
          <tbody>
            {t.splits.map((s) => (
              <tr key={s.id}>
                <td>
                  {s.categoryName} <span className="muted">({typeLabel(s.type)})</span>
                  {s.note && <span className="cell-note">{s.note}</span>}
                </td>
                <td className="num">
                  <Money cents={s.amountCents} />
                </td>
              </tr>
            ))}
            <tr className="total">
              <td>Remaining unassigned</td>
              <td className="num">
                <Money cents={t.unassignedCents} />
              </td>
            </tr>
          </tbody>
        </table>
      )}

      {t.raw && (
        <>
          <h3>Original row</h3>
          <dl className="kv">
            {Object.entries(t.raw)
              .filter(([, v]) => v !== '')
              .map(([k, v]) => (
                <div key={k} style={{ display: 'contents' }}>
                  <dt>{k}</dt>
                  <dd>{v}</dd>
                </div>
              ))}
          </dl>
        </>
      )}

      <h3>History</h3>
      {t.audit.length === 0 ? <p className="muted">No changes yet.</p> : (
        <ul>
          {t.audit.map((a) => (
            <li key={a.id}>
              {dateLabel(a.at.slice(0, 10))} · {a.action} {a.user ? `by ${a.user}` : ''}
            </li>
          ))}
        </ul>
      )}

      <p style={{ marginTop: 16 }}>
        <Link to={`/lines?from=${t.bank.postedOn}&to=${t.bank.postedOn}&account=${t.bank.accountId}&move=include`}>Everything on this account that day</Link>
      </p>
    </>
  )
}
