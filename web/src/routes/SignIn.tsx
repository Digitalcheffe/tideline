import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError, get, send, setCsrf, type Me } from '../api'
import { Badge, Loading } from '../components'
import { LogoMark, Wordmark } from '../logo'

function Shell({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="signin">
      <div className="signin-card">
        <div className="signin-brand">
          <LogoMark />
          <Wordmark />
        </div>
        <h1>{title}</h1>
        {children}
      </div>
    </main>
  )
}

function Problem({ error }: { error: unknown }) {
  if (!error) return null
  return (
    <p className="problem" role="alert">
      {error instanceof Error ? error.message : String(error)}
    </p>
  )
}

/** Ask the server who is signed in now, then let the gate show the right page. */
function useRefreshMe() {
  const client = useQueryClient()
  return async () => {
    await client.invalidateQueries({ queryKey: ['me'] })
  }
}

// ---- password ---------------------------------------------------------------------------------------------

export function LoginPage() {
  const refresh = useRefreshMe()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')

  // an app with nobody in it asks for the first account instead of a sign-in
  const first = useQuery({ queryKey: ['first-run'], queryFn: () => get<{ needed: boolean; minPasswordLength: number }>('/auth/first-run'), retry: false })

  const login = useMutation({
    mutationFn: () => send<{ mfaRequired: boolean }>('POST', '/auth/login', { email, password }),
    onSuccess: refresh,
  })

  if (first.isLoading) return <Shell title="Sign in"><Loading /></Shell>
  if (first.data?.needed) return <FirstRunPage minLength={first.data.minPasswordLength} />

  return (
    <Shell title="Sign in">
      <form
        className="form"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          login.mutate()
        }}
      >
        <label>
          Email
          <input type="email" autoComplete="username" autoFocus required value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label>
          Password
          <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        <button className="btn primary" type="submit" disabled={login.isPending}>
          {login.isPending ? 'Signing in…' : 'Continue'}
        </button>
        <Problem error={login.error} />
      </form>
      <p className="muted">There is no sign-up page. Someone with access to the server adds you and gives you a setup link.</p>
    </Shell>
  )
}

// ---- first run ----------------------------------------------------------------------------------------------

/** Nobody exists yet, so whoever is here makes the owner account. The authenticator is set up straight afterwards, like for everyone. */
function FirstRunPage({ minLength }: { minLength: number }) {
  const client = useQueryClient()
  const [email, setEmail] = useState('')
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [again, setAgain] = useState('')
  const mismatch = again !== '' && again !== password

  const create = useMutation({
    mutationFn: () => send('POST', '/auth/first-run', { email, displayName: name, password }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['first-run'] })
      await client.invalidateQueries({ queryKey: ['me'] })
    },
  })

  return (
    <Shell title="Welcome to Tideline">
      <p>
        Nothing is set up yet. Create the first account: you will be the owner, and can invite other people later. Next you will set up an authenticator
        app, which is needed every time you sign in.
      </p>
      <form
        className="form"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (!mismatch) create.mutate()
        }}
      >
        <label>
          Your name
          <input type="text" autoComplete="name" autoFocus required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Email <span className="muted">(this is what you sign in with)</span>
          <input type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label>
          Password <span className="muted">(at least {minLength} characters; a few random words works well)</span>
          <input type="password" autoComplete="new-password" required minLength={minLength} value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        <label>
          Again
          <input type="password" autoComplete="new-password" required value={again} onChange={(e) => setAgain(e.target.value)} />
        </label>
        {mismatch && <Badge tone="warn">The two do not match</Badge>}
        <button className="btn primary" type="submit" disabled={create.isPending || mismatch || password.length < minLength}>
          {create.isPending ? 'Creating…' : 'Create the account'}
        </button>
        <Problem error={create.error} />
      </form>
    </Shell>
  )
}

// ---- second step --------------------------------------------------------------------------------------------

export function MfaPage({ me }: { me: Me }) {
  const refresh = useRefreshMe()
  const [code, setCode] = useState('')
  const [useRecovery, setUseRecovery] = useState(false)

  const verify = useMutation({
    mutationFn: () => send('POST', '/auth/mfa/totp', useRecovery ? { recoveryCode: code } : { code }),
    onSuccess: refresh,
    onError: () => setCode(''),
  })

  return (
    <Shell title="Second step">
      <p>
        Signed in as <strong>{me.user.displayName}</strong>. {useRecovery ? 'Type one of your recovery codes.' : 'Type the 6 digit code from your authenticator app.'}
      </p>
      <form
        className="form"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          verify.mutate()
        }}
      >
        <label>
          {useRecovery ? 'Recovery code' : 'Code'}
          <input
            type="text"
            inputMode={useRecovery ? 'text' : 'numeric'}
            autoComplete="one-time-code"
            autoFocus
            required
            placeholder={useRecovery ? 'xxxxx-xxxxx' : '123456'}
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
        </label>
        <button className="btn primary" type="submit" disabled={verify.isPending || code.trim() === ''}>
          {verify.isPending ? 'Checking…' : 'Sign in'}
        </button>
        <Problem error={verify.error} />
      </form>
      <p>
        <button className="linklike" type="button" onClick={() => { setUseRecovery(!useRecovery); setCode(''); verify.reset() }}>
          {useRecovery ? 'Use the authenticator app instead' : 'Lost your phone? Use a recovery code'}
        </button>
      </p>
      <SignOutLink />
    </Shell>
  )
}

function SignOutLink() {
  const client = useQueryClient()
  return (
    <p>
      <button
        className="linklike"
        type="button"
        onClick={async () => {
          await send('POST', '/auth/logout').catch(() => undefined)
          setCsrf(null)
          client.setQueryData(['me'], null)
        }}
      >
        Not you? Sign out
      </button>
    </p>
  )
}

// ---- setting up the authenticator ---------------------------------------------------------------------------

interface Enrollment {
  secret: string
  otpauthUri: string
  qrSvg: string
}

export function EnrollPage({ me }: { me: Me }) {
  const refresh = useRefreshMe()
  const [code, setCode] = useState('')
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const started = useRef(false)

  const start = useMutation({ mutationFn: () => send<Enrollment>('POST', '/auth/enroll/totp/start') })
  useEffect(() => {
    if (!started.current) {
      started.current = true
      start.mutate()
    }
  }, [start])

  const confirm = useMutation({
    mutationFn: () => send<{ recoveryCodes: string[] }>('POST', '/auth/enroll/totp/confirm', { code }),
    onSuccess: (r) => setRecoveryCodes(r.recoveryCodes),
  })

  if (recoveryCodes)
    return (
      <Shell title="Save your recovery codes">
        <p>
          If you lose your phone, each of these signs you in <strong>once</strong>. They are shown only now. Keep them somewhere safe, such as a password manager.
        </p>
        <ul className="codes">
          {recoveryCodes.map((c) => (
            <li key={c}>{c}</li>
          ))}
        </ul>
        <p className="toolbar">
          <button className="btn" type="button" onClick={() => navigator.clipboard?.writeText(recoveryCodes.join('\n'))}>
            Copy
          </button>
          <button className="btn primary" type="button" onClick={refresh}>
            I have saved them
          </button>
        </p>
      </Shell>
    )

  return (
    <Shell title="Set up your authenticator">
      <p>
        Hi {me.user.displayName}. Signing in needs a code from an authenticator app (1Password, Google Authenticator, Authy and so on) as well as your password. This cannot be turned off.
      </p>
      {start.isPending && <Loading />}
      <Problem error={start.error} />
      {start.data && (
        <>
          <ol>
            <li>Open your authenticator app and add an account by scanning this code.</li>
          </ol>
          <div className="qr" aria-label="QR code for your authenticator app" dangerouslySetInnerHTML={{ __html: start.data.qrSvg }} />
          <details>
            <summary>Cannot scan? Type the key instead</summary>
            <p>
              <code className="secret">{start.data.secret.replace(/(.{4})/g, '$1 ').trim()}</code>
            </p>
          </details>
          <form
            className="form"
            onSubmit={(e: FormEvent) => {
              e.preventDefault()
              confirm.mutate()
            }}
          >
            <label>
              Then type the 6 digit code it shows
              <input type="text" inputMode="numeric" autoComplete="one-time-code" required placeholder="123456" value={code} onChange={(e) => setCode(e.target.value)} />
            </label>
            <button className="btn primary" type="submit" disabled={confirm.isPending || code.trim() === ''}>
              {confirm.isPending ? 'Checking…' : 'Turn it on'}
            </button>
            <Problem error={confirm.error} />
          </form>
        </>
      )}
      <SignOutLink />
    </Shell>
  )
}

// ---- following a setup link ---------------------------------------------------------------------------------

export function SetupPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const refresh = useRefreshMe()
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [again, setAgain] = useState('')

  const info = useQuery({
    queryKey: ['setup', token],
    queryFn: () => get<{ email: string; displayName: string; minPasswordLength: number }>('/auth/setup', { token }),
    retry: false,
  })

  const setup = useMutation({
    mutationFn: () => send('POST', '/auth/setup', { token, password }),
    onSuccess: async () => {
      await refresh()
      navigate('/', { replace: true })
    },
  })

  if (info.isLoading) return <Shell title="Welcome"><Loading /></Shell>
  if (info.error || !info.data)
    return (
      <Shell title="This link does not work">
        <p>{info.error instanceof ApiError ? info.error.message : 'This setup link has been used or has run out.'}</p>
        <p className="muted">Ask the owner for a new one.</p>
      </Shell>
    )

  const mismatch = again !== '' && again !== password
  return (
    <Shell title={`Welcome, ${info.data.displayName}`}>
      <p>
        Choose a password for <strong>{info.data.email}</strong>. Then you will set up an authenticator app.
      </p>
      <form
        className="form"
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (!mismatch) setup.mutate()
        }}
      >
        <label>
          Password <span className="muted">(at least {info.data.minPasswordLength} characters; a few random words works well)</span>
          <input type="password" autoComplete="new-password" autoFocus required minLength={info.data.minPasswordLength} value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        <label>
          Again
          <input type="password" autoComplete="new-password" required value={again} onChange={(e) => setAgain(e.target.value)} />
        </label>
        {mismatch && <Badge tone="warn">The two do not match</Badge>}
        <button className="btn primary" type="submit" disabled={setup.isPending || mismatch || password.length < info.data.minPasswordLength}>
          {setup.isPending ? 'Saving…' : 'Continue'}
        </button>
        <Problem error={setup.error} />
      </form>
    </Shell>
  )
}
