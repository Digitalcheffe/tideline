import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { get, saveFile, send } from '../../api'
import { Badge, Card, ErrorBox, Loading } from '../../components'
import { Problem, useAct, when } from './common'

interface BackupsPage {
  folder: string
  keepDays: number
  files: { name: string; bytes: number; modified: string; kind: 'database' | 'keys' }[]
  lastBackupAt: string | null
  overdue: boolean
}

interface Made {
  file: string
  bytes: number
  keysIncluded: boolean
  ok: boolean
  message: string
}

const size = (bytes: number) => (bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`)

export function DataSettings() {
  const backups = useQuery({ queryKey: ['settings', 'backups'], queryFn: () => get<BackupsPage>('/backups') })
  const backup = useAct()
  const exporting = useAct()
  const [made, setMade] = useState<Made | null>(null)

  if (backups.isLoading) return <Loading what="backups" />
  if (backups.error || !backups.data) return <ErrorBox error={backups.error} />
  const b = backups.data

  return (
    <>
      <Card
        title="Backups"
        aside={
          <button
            type="button"
            className="btn primary"
            disabled={backup.busy}
            onClick={async () => setMade((await backup.run(() => send<Made>('POST', '/backups'))) ?? null)}
          >
            {backup.busy ? 'Backing up…' : 'Back up now'}
          </button>
        }
      >
        <p>
          {b.lastBackupAt ? (
            <>
              Last backup {when(b.lastBackupAt)}. {b.overdue ? <Badge tone="warn">more than two days ago</Badge> : <Badge tone="good">recent</Badge>}
            </>
          ) : (
            <>
              No backup yet. <Badge tone="warn">make one</Badge>
            </>
          )}
        </p>
        <p className="muted">
          Copies go to <code>{b.folder}</code> and the last {b.keepDays} days are kept. Each one holds the database; the keys that unlock the
          authenticator secrets are saved beside it, because without them a restored copy cannot sign anyone in. Keep both off this computer too.
        </p>
        <Problem message={backup.error} />
        {made && (
          <p role="status">
            <Badge tone={made.ok ? 'good' : 'bad'}>{made.ok ? 'Checked' : 'Problem'}</Badge> {made.file} ({size(made.bytes)}). {made.message}
          </p>
        )}
        {b.files.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>File</th>
                  <th>What</th>
                  <th>Made</th>
                  <th className="num">Size</th>
                </tr>
              </thead>
              <tbody>
                {b.files.map((f) => (
                  <tr key={f.name}>
                    <td>{f.name}</td>
                    <td>{f.kind === 'database' ? 'Database' : 'Keys'}</td>
                    <td>{when(f.modified)}</td>
                    <td className="num">{size(f.bytes)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <h3>Getting one back</h3>
        <p className="muted">
          Stop the app, then run <code>tideline restore &lt;backup file&gt; --keys-tar &lt;keys file&gt; --keys-dir &lt;keys folder&gt; --force</code> and start it again.
          It checks the copy first, refuses a damaged one, and prints the year's spread so you can see it matches. To only check a copy, run{' '}
          <code>tideline backup --verify &lt;backup file&gt;</code>.
        </p>
      </Card>

      <Card title="Take everything out">
        <p className="muted">
          One zip with a CSV for every table: lines, accounts, rules, the audit log and the rest. Passwords, authenticator secrets, recovery codes, sessions and
          API tokens are left out. Taking an export is written to the audit log.
        </p>
        <button type="button" className="btn" disabled={exporting.busy} onClick={() => void exporting.run(() => saveFile('/export', 'tideline-export.zip'))}>
          {exporting.busy ? 'Preparing…' : 'Download the export'}
        </button>
        <Problem message={exporting.error} />
      </Card>
    </>
  )
}
