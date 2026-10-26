import { useState, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { get, send } from '../api'
import { useMe } from '../auth'
import { Badge, Card, Empty, ErrorBox, Loading } from '../components'
import { Avatar, Icon, type IconName } from '../icons'

interface Device {
  id: number
  createdAt: string
  lastSeenAt: string
  ip: string | null
  userAgent: string | null
  isCurrent: boolean
}

/** "Edge on Windows" from a browser's long identifier. A guess, kept short, for telling your own devices apart. */
function describeDevice(agent: string | null): string {
  if (!agent) return 'A device'
  const browser = /Edg\//.test(agent) ? 'Edge' : /OPR\/|Opera/.test(agent) ? 'Opera' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome' : /Safari\//.test(agent) ? 'Safari' : 'A browser'
  const system = /iPhone|iPad/.test(agent) ? 'iPhone or iPad' : /Android/.test(agent) ? 'Android' : /Windows/.test(agent) ? 'Windows' : /Mac OS X|Macintosh/.test(agent) ? 'a Mac' : /Linux/.test(agent) ? 'Linux' : ''
  return system ? `${browser} on ${system}` : browser
}

const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })

function Section({ icon, title, children }: { icon: IconName; title: string; children: ReactNode }) {
  return (
    <Card
      title={
        <span className="with-icon">
          <Icon name={icon} /> {title}
        </span>
      }
    >
      {children}
    </Card>
  )
}

function Problem({ error }: { error: unknown }) {
  return error ? (
    <p className="problem" role="alert">
      <Icon name="warning" size={16} /> {error instanceof Error ? error.message : String(error)}
    </p>
  ) : null
}

function Codes({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  return (
    <>
      <p>
        <Icon name="key" size={16} /> Here are your new recovery codes. Each signs you in <strong>once</strong>, and the old ones no longer work. They are shown only now, so keep them somewhere
        safe, such as a password manager.
      </p>
      <ul className="codes">
        {codes.map((c) => (
          <li key={c}>{c}</li>
        ))}
      </ul>
      <div className="toolbar">
        <button className="btn" type="button" onClick={() => navigator.clipboard?.writeText(codes.join('\n'))}>
          <Icon name="copy" size={16} /> Copy
        </button>
        <button className="btn primary" type="button" onClick={onDone}>
          I have saved them
        </button>
      </div>
    </>
  )
}

/** Your own sign-in: who you are, your password, your authenticator, your recovery codes and the devices signed in as you. */
export function Account() {
  const me = useMe()
  return (
    <>
      <h1>Your account</h1>
      <div className="account-head">
        <Avatar name={me.user.displayName} size={64} />
        <div>
          <strong className="account-name">{me.user.displayName}</strong>
          <div className="muted">
            {me.user.email} · {me.user.role}
          </div>
        </div>
      </div>
      <NameSection />
      <PasswordSection />
      <AuthenticatorSection />
      <RecoverySection />
      <DevicesSection />
    </>
  )
}

function NameSection() {
  const me = useMe()
  const client = useQueryClient()
  const [name, setName] = useState(me.user.displayName)
  const save = useMutation({
    mutationFn: () => send('PATCH', '/auth/profile', { displayName: name }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['me'] }),
  })
  return (
    <Section icon="user" title="Your name">
      <form
        className="form"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          save.mutate()
        }}
      >
        <label>
          Name
          <input type="text" required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={save.isPending || name.trim() === '' || name.trim() === me.user.displayName}>
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
          {save.isSuccess && <Badge tone="good">Saved</Badge>}
        </div>
        <Problem error={save.error} />
      </form>
    </Section>
  )
}

function PasswordSection() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const mismatch = again !== '' && again !== next
  const change = useMutation({
    mutationFn: () => send('POST', '/auth/password', { currentPassword: current, newPassword: next }),
    onSuccess: () => {
      setCurrent('')
      setNext('')
      setAgain('')
    },
  })
  return (
    <Section icon="lock" title="Password">
      <p className="muted">Changing it signs you out on every other device. This one stays signed in.</p>
      <form
        className="form"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (!mismatch) change.mutate()
        }}
      >
        <label>
          Current password
          <input type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} />
        </label>
        <label>
          New password <span className="muted">(at least 12 characters; a few random words works well)</span>
          <input type="password" autoComplete="new-password" required minLength={12} value={next} onChange={(e) => setNext(e.target.value)} />
        </label>
        <label>
          New password again
          <input type="password" autoComplete="new-password" required value={again} onChange={(e) => setAgain(e.target.value)} />
        </label>
        {mismatch && <Badge tone="warn">The two do not match</Badge>}
        <div className="toolbar">
          <button className="btn primary" type="submit" disabled={change.isPending || mismatch || next.length < 12 || current === ''}>
            {change.isPending ? 'Changing…' : 'Change password'}
          </button>
          {change.isSuccess && <Badge tone="good">Password changed</Badge>}
        </div>
        <Problem error={change.error} />
      </form>
    </Section>
  )
}

function AuthenticatorSection() {
  const client = useQueryClient()
  const [step, setStep] = useState<'idle' | 'prove' | 'scan' | 'done'>('idle')
  const [useRecovery, setUseRecovery] = useState(false)
  const [proof, setProof] = useState('')
  const [fresh, setFresh] = useState('')
  const [codes, setCodes] = useState<string[]>([])

  const start = useMutation({
    mutationFn: () => send<{ secret: string; qrSvg: string }>('POST', '/auth/authenticator/start', useRecovery ? { recoveryCode: proof } : { code: proof }),
    onSuccess: () => setStep('scan'),
  })
  const confirm = useMutation({
    mutationFn: () => send<{ recoveryCodes: string[] }>('POST', '/auth/authenticator/confirm', { code: fresh }),
    onSuccess: (r) => {
      setCodes(r.recoveryCodes)
      setStep('done')
      client.invalidateQueries({ queryKey: ['me'] })
      client.invalidateQueries({ queryKey: ['my-sessions'] })
    },
  })
  const reset = () => {
    setStep('idle')
    setProof('')
    setFresh('')
    setUseRecovery(false)
    start.reset()
    confirm.reset()
  }

  return (
    <Section icon="phone" title="Authenticator app">
      {step === 'idle' && (
        <>
          <p>
            <Icon name="check" size={16} /> An authenticator app gives the code you type after your password. Got a new phone? Set it up here, and the old one stops working.
          </p>
          <button className="btn" type="button" onClick={() => setStep('prove')}>
            Set up a new phone
          </button>
        </>
      )}

      {step === 'prove' && (
        <form
          className="form"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            start.mutate()
          }}
        >
          <p className="muted">First, show it is you.</p>
          <label>
            {useRecovery ? 'One of your recovery codes' : 'The code from your current authenticator app'}
            <input
              type="text"
              inputMode={useRecovery ? 'text' : 'numeric'}
              autoComplete="one-time-code"
              required
              placeholder={useRecovery ? 'xxxxx-xxxxx' : '123456'}
              value={proof}
              onChange={(e) => setProof(e.target.value)}
            />
          </label>
          <div className="toolbar">
            <button className="btn primary" type="submit" disabled={start.isPending || proof.trim() === ''}>
              {start.isPending ? 'Checking…' : 'Continue'}
            </button>
            <button className="btn" type="button" onClick={reset}>
              Cancel
            </button>
            <button className="linklike" type="button" onClick={() => { setUseRecovery(!useRecovery); setProof(''); start.reset() }}>
              {useRecovery ? 'Use the app instead' : 'Lost your old phone? Use a recovery code'}
            </button>
          </div>
          <Problem error={start.error} />
        </form>
      )}

      {step === 'scan' && start.data && (
        <form
          className="form"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            confirm.mutate()
          }}
        >
          <p>Open the authenticator app on the new phone, add an account, and scan this code.</p>
          <div className="qr" aria-label="QR code for your new authenticator" dangerouslySetInnerHTML={{ __html: start.data.qrSvg }} />
          <details>
            <summary>Cannot scan? Type the key instead</summary>
            <p>
              <code className="secret">{start.data.secret.replace(/(.{4})/g, '$1 ').trim()}</code>
            </p>
          </details>
          <label>
            Then type the 6 digit code it shows
            <input type="text" inputMode="numeric" autoComplete="one-time-code" required placeholder="123456" value={fresh} onChange={(e) => setFresh(e.target.value)} />
          </label>
          <div className="toolbar">
            <button className="btn primary" type="submit" disabled={confirm.isPending || fresh.trim() === ''}>
              {confirm.isPending ? 'Checking…' : 'Switch to the new phone'}
            </button>
            <button className="btn" type="button" onClick={reset}>
              Cancel
            </button>
          </div>
          <Problem error={confirm.error} />
        </form>
      )}

      {step === 'done' && <Codes codes={codes} onDone={reset} />}
    </Section>
  )
}

function RecoverySection() {
  const me = useMe()
  const client = useQueryClient()
  const [asking, setAsking] = useState(false)
  const [code, setCode] = useState('')
  const [codes, setCodes] = useState<string[] | null>(null)
  const make = useMutation({
    mutationFn: () => send<{ recoveryCodes: string[] }>('POST', '/auth/recovery-codes', { code }),
    onSuccess: (r) => {
      setCodes(r.recoveryCodes)
      setAsking(false)
      setCode('')
      client.invalidateQueries({ queryKey: ['me'] })
    },
  })
  const low = me.recoveryCodesLeft <= 2
  return (
    <Section icon="key" title="Recovery codes">
      {codes ? (
        <Codes codes={codes} onDone={() => setCodes(null)} />
      ) : (
        <>
          <p>
            {low ? <Icon name="warning" size={16} /> : <Icon name="shield" size={16} />} You have <strong>{me.recoveryCodesLeft}</strong> unused {me.recoveryCodesLeft === 1 ? 'code' : 'codes'}. They are for the day your
            phone is lost.
          </p>
          {!asking ? (
            <button className="btn" type="button" onClick={() => setAsking(true)}>
              Make 10 new codes
            </button>
          ) : (
            <form
              className="form"
              onSubmit={(e: FormEvent) => {
                e.preventDefault()
                make.mutate()
              }}
            >
              <label>
                The code from your authenticator app <span className="muted">(new codes replace the old ones)</span>
                <input type="text" inputMode="numeric" autoComplete="one-time-code" required placeholder="123456" value={code} onChange={(e) => setCode(e.target.value)} />
              </label>
              <div className="toolbar">
                <button className="btn primary" type="submit" disabled={make.isPending || code.trim() === ''}>
                  {make.isPending ? 'Making…' : 'Make new codes'}
                </button>
                <button className="btn" type="button" onClick={() => { setAsking(false); setCode(''); make.reset() }}>
                  Cancel
                </button>
              </div>
              <Problem error={make.error} />
            </form>
          )}
        </>
      )}
    </Section>
  )
}

function DevicesSection() {
  const client = useQueryClient()
  const list = useQuery({ queryKey: ['my-sessions'], queryFn: () => get<Device[]>('/auth/sessions') })
  const refresh = () => client.invalidateQueries({ queryKey: ['my-sessions'] })
  const end = useMutation({ mutationFn: (d: Device) => send('DELETE', `/auth/sessions/${d.id}`), onSuccess: refresh })
  const others = useMutation({ mutationFn: () => send('POST', '/auth/sessions/revoke-others'), onSuccess: refresh })

  return (
    <Section icon="devices" title="Where you are signed in">
      {list.isLoading && <Loading what="your devices" />}
      {list.error && <ErrorBox error={list.error} />}
      {list.data && list.data.length === 0 && <Empty>No other sessions.</Empty>}
      {list.data && list.data.length > 0 && (
        <ul className="devices">
          {list.data.map((d) => (
            <li key={d.id}>
              <Icon name={/iPhone|Android|Mobile/.test(d.userAgent ?? '') ? 'phone' : 'devices'} />
              <div>
                <strong>{describeDevice(d.userAgent)}</strong> {d.isCurrent && <Badge tone="info">this device</Badge>}
                <div className="muted">
                  last used {when(d.lastSeenAt)}
                  {d.ip ? ` · ${d.ip}` : ''}
                </div>
              </div>
              {!d.isCurrent && (
                <button className="btn" type="button" disabled={end.isPending} onClick={() => end.mutate(d)}>
                  <Icon name="logout" size={16} /> Sign out
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {list.data && list.data.length > 1 && (
        <div className="toolbar">
          <button className="btn" type="button" disabled={others.isPending} onClick={() => others.mutate()}>
            Sign out everywhere else
          </button>
        </div>
      )}
      <Problem error={end.error ?? others.error} />
    </Section>
  )
}
