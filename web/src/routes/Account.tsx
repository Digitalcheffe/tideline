import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { get, send } from '../api'
import { useMe } from '../auth'
import { Card, Empty, ErrorBox, Loading } from '../components'
import { dateLabel } from '../format'
import { addPasskey, passkeysAvailable } from '../webauthn'

interface Passkey {
  id: number
  label: string
  createdAt: string
  lastUsedAt: string | null
}

/** Your own sign-in: the passkeys on your account, and how many recovery codes are left. */
export function Account() {
  const me = useMe()
  const client = useQueryClient()
  const list = useQuery({ queryKey: ['passkeys'], queryFn: () => get<Passkey[]>('/auth/passkeys') })
  const [label, setLabel] = useState('')

  const add = useMutation({
    mutationFn: () => addPasskey(label.trim() || 'Passkey'),
    onSuccess: () => {
      setLabel('')
      client.invalidateQueries({ queryKey: ['passkeys'] })
      client.invalidateQueries({ queryKey: ['me'] })
    },
  })
  const remove = useMutation({
    mutationFn: (p: Passkey) => send('DELETE', `/auth/passkeys/${p.id}`),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ['passkeys'] })
      client.invalidateQueries({ queryKey: ['me'] })
    },
  })

  return (
    <>
      <h1>Your sign-in</h1>
      <p className="muted">
        You sign in with your password and then a second step. The second step cannot be turned off. You have {me.recoveryCodesLeft} recovery{' '}
        {me.recoveryCodesLeft === 1 ? 'code' : 'codes'} left.
      </p>

      <Card title="Passkeys">
        <p className="muted">
          A passkey is a fingerprint, a face, a PIN or a security key. It works only on the address Tideline is set up for, and the secret part never
          leaves your device.
        </p>
        {list.isLoading && <Loading what="your passkeys" />}
        {list.error && <ErrorBox error={list.error} />}
        {list.data && list.data.length === 0 && <Empty>No passkeys yet.</Empty>}
        {list.data && list.data.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Added</th>
                  <th>Last used</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((p) => (
                  <tr key={p.id}>
                    <td>{p.label}</td>
                    <td>{dateLabel(p.createdAt.slice(0, 10))}</td>
                    <td>{p.lastUsedAt ? dateLabel(p.lastUsedAt.slice(0, 10)) : <span className="muted">never</span>}</td>
                    <td>
                      <button
                        className="btn"
                        type="button"
                        disabled={remove.isPending}
                        onClick={() => window.confirm(`Remove the passkey “${p.label}”?`) && remove.mutate(p)}
                      >
                        Remove
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {remove.error && <p className="problem" role="alert">{remove.error.message}</p>}

        {passkeysAvailable() ? (
          <form
            className="form"
            onSubmit={(e) => {
              e.preventDefault()
              add.mutate()
            }}
          >
            <label>
              Name for the new passkey <span className="muted">(for example, Phone or Laptop)</span>
              <input type="text" maxLength={100} value={label} onChange={(e) => setLabel(e.target.value)} />
            </label>
            <div className="toolbar">
              <button className="btn primary" type="submit" disabled={add.isPending}>
                {add.isPending ? 'Waiting for your device…' : 'Add a passkey'}
              </button>
            </div>
            {add.error && <p className="problem" role="alert">{add.error.message}</p>}
          </form>
        ) : (
          <p className="muted">This browser cannot make passkeys. Use one that does, on the address Tideline is set up for.</p>
        )}
      </Card>
    </>
  )
}
