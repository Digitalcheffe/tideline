import fs from 'node:fs'
import { request } from '@playwright/test'
import { AUTH_FILE, LINK_FILE, OWNER_EMAIL, PASSWORD, STATE_FILE, WEB_PORT, totp, writeAuth } from './helpers'

/**
 * The owner follows their setup link, chooses a password and enrols an authenticator, exactly as a person would. The signed-in
 * session is saved so the other tests start signed in (a code works once, so they cannot each do the second step).
 */
export default async function globalSetup() {
  const { url } = JSON.parse(fs.readFileSync(LINK_FILE, 'utf8')) as { url: string }
  const token = new URL(url).searchParams.get('token')!

  const ctx = await request.newContext({ baseURL: `http://localhost:${WEB_PORT}` })
  const check = async (what: string, response: { ok(): boolean; status(): number; text(): Promise<string> }) => {
    if (!response.ok()) throw new Error(`${what} failed: ${response.status()} ${await response.text()}`)
  }

  await check('setup', await ctx.post('/api/v1/auth/setup', { data: { token, password: PASSWORD } }))
  const me = await (await ctx.get('/api/v1/auth/me')).json()
  const headers = { 'X-CSRF-Token': me.csrf as string }

  const start = await ctx.post('/api/v1/auth/enroll/totp/start', { headers })
  await check('enrol', start)
  const secret = (await start.json()).secret as string

  const step = Math.floor(Date.now() / 30000)
  const confirm = await ctx.post('/api/v1/auth/enroll/totp/confirm', { headers, data: { code: totp(secret, step) } })
  await check('confirm', confirm)
  const recoveryCodes = (await confirm.json()).recoveryCodes as string[]

  writeAuth({ email: OWNER_EMAIL, password: PASSWORD, secret, recoveryCodes, lastStep: step })
  await ctx.storageState({ path: STATE_FILE })
  await ctx.dispose()
  void AUTH_FILE
}
