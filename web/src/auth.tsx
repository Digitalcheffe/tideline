import { createContext, useContext, useEffect, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useLocation } from 'react-router-dom'
import { getMe, send, setCsrf, setOnUnauthorized, type Me } from './api'
import { Loading } from './components'
import { EnrollPage, LoginPage, MfaPage, SetupPage } from './routes/SignIn'

const MeContext = createContext<Me | null>(null)

/** The person signed in. Only available inside the gate, so it is never null there. */
export function useMe(): Me {
  const me = useContext(MeContext)
  if (!me) throw new Error('useMe was used outside the sign-in gate')
  return me
}

export function useSignOut() {
  const client = useQueryClient()
  return async () => {
    try {
      await send('POST', '/auth/logout')
    } finally {
      setCsrf(null)
      // show the sign-in page first, then forget everything that was loaded for the person who left
      client.setQueryData(['me'], null)
      client.removeQueries({ predicate: (q) => q.queryKey[0] !== 'me' })
    }
  }
}

/**
 * Nothing in the app shows until the person has signed in and passed the second step. Where they are in that is decided by
 * the server (/auth/me); this only shows the right page.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  const client = useQueryClient()
  const location = useLocation()
  const { data: me, isLoading, error } = useQuery({ queryKey: ['me'], queryFn: getMe, retry: false, refetchOnWindowFocus: true })

  useEffect(() => setCsrf(me?.csrf ?? null), [me])
  useEffect(() => {
    setOnUnauthorized(() => client.setQueryData(['me'], null))
    return () => setOnUnauthorized(null)
  }, [client])

  if (location.pathname === '/setup' && !me?.mfaPassed) return <SetupPage />
  if (isLoading) return <Loading />
  if (error) return <div className="state error" role="alert">Could not reach Tideline. Is it running? ({error.message})</div>
  if (!me) return <LoginPage />
  if (!me.mfaPassed) return me.mfaEnrolled ? <MfaPage me={me} /> : <EnrollPage me={me} />
  return <MeContext.Provider value={me}>{children}</MeContext.Provider>
}
