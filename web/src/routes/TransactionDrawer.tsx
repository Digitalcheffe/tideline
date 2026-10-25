import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { get, type TransactionDetail } from '../api'
import { Badge, ErrorBox, Loading, Money } from '../components'
import { dateLabel, typeLabel } from '../format'
import { EditPanel } from './EditPanel'

/** The transaction drawer (docs/08-screens.md, "Lines"). Editing arrives with the splits editor. */
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

function Detail({ t }: { t: TransactionDetail }) {
  return (
    <>
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
        <p>
          <Badge tone="muted">Transfer</Badge> The other side is <strong>{t.movePartner.accountName}</strong> on {dateLabel(t.movePartner.postedOn)}:{' '}
          <Money cents={t.movePartner.amountCents} /> “{t.movePartner.description}”. Transfers between your own accounts are not counted as money in or out.
        </p>
      ) : t.moveId ? (
        <p>
          <Badge tone="warn">Waiting for its other side</Badge> This looks like a transfer. It pairs when the other account's export is imported.
        </p>
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

      {!t.moveId && <EditPanel t={t} />}

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
