import { createHmac } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export const API_PORT = 5190
export const WEB_PORT = 5191
export const E2E_DB = path.join(os.tmpdir(), 'tideline-e2e.db')
export const LINK_FILE = path.join(os.tmpdir(), 'tideline-e2e-link.json')
export const AUTH_FILE = path.join(os.tmpdir(), 'tideline-e2e-auth.json')
export const STATE_FILE = path.join(os.tmpdir(), 'tideline-e2e-state.json')
export const OWNER_EMAIL = 'owner@example.com'
export const PASSWORD = 'a long passphrase of mine'

const repo = path.resolve(import.meta.dirname, '..', '..')

/** Run the tideline command line against the throwaway database. */
export function cli(...args: string[]): string {
  try {
    return execFileSync('dotnet', ['run', '--project', 'src/Tideline.Cli', '--no-build', '--', ...args, '--db', `Data Source=${E2E_DB}`], { cwd: repo, stdio: 'pipe' }).toString()
  } catch (e) {
    const err = e as { stdout?: Buffer; stderr?: Buffer }
    throw new Error(`tideline ${args.join(' ')} failed:\n${err.stdout?.toString() ?? ''}\n${err.stderr?.toString() ?? ''}`)
  }
}

export function linkFrom(output: string): string {
  const m = output.match(/https?:\/\/\S+\/setup\?token=\S+/)
  if (!m) throw new Error(`no setup link in:\n${output}`)
  return m[0]
}

function base32Decode(text: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
  let bits = 0
  let value = 0
  const out: number[] = []
  for (const c of text.replace(/[\s=-]/g, '').toUpperCase()) {
    value = (value << 5) | alphabet.indexOf(c)
    bits += 5
    if (bits >= 8) {
      out.push((value >>> (bits - 8)) & 0xff)
      bits -= 8
    }
  }
  return Buffer.from(out)
}

/** RFC 6238, the same code an authenticator app shows. */
export function totp(secret: string, step: number): string {
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(step))
  const h = createHmac('sha1', base32Decode(secret)).update(counter).digest()
  const o = h[h.length - 1] & 0x0f
  const n = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3]
  return String(n % 1_000_000).padStart(6, '0')
}

export interface AuthInfo {
  email: string
  password: string
  secret: string
  recoveryCodes: string[]
  lastStep: number
}

export function readAuth(): AuthInfo {
  return JSON.parse(fs.readFileSync(AUTH_FILE, 'utf8')) as AuthInfo
}

export function writeAuth(info: AuthInfo) {
  fs.writeFileSync(AUTH_FILE, JSON.stringify(info))
}

/**
 * A code that has not been used before. Each code works once, so after one has been used the next test waits for the next
 * 30 second window instead of being refused as a replay.
 */
export async function freshCode(secret: string, lastStep: number): Promise<{ code: string; step: number }> {
  let step = Math.floor(Date.now() / 30000)
  if (step <= lastStep) {
    await new Promise((r) => setTimeout(r, (lastStep + 1) * 30000 - Date.now() + 250))
    step = Math.floor(Date.now() / 30000)
  }
  return { code: totp(secret, step), step }
}
