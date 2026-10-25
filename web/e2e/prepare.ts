import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { E2E_DB, LINK_FILE, OWNER_EMAIL, WEB_PORT, cli, linkFrom } from './helpers'

/**
 * Build the API and CLI, then load the made-up March 2026 household into a fresh database, with an owner waiting to set up
 * from a link. Runs when the Playwright config loads, which is before the web servers start (they would lock the build output).
 */
export function prepare() {
  if (process.env.TIDELINE_E2E_READY) return
  const repo = path.resolve(import.meta.dirname, '..', '..')

  const run = (args: string[]) => {
    try {
      return execFileSync('dotnet', args, { cwd: repo, stdio: 'pipe' })
    } catch (e) {
      const err = e as { stdout?: Buffer; stderr?: Buffer }
      throw new Error(`dotnet ${args.join(' ')} failed:\n${err.stdout?.toString() ?? ''}\n${err.stderr?.toString() ?? ''}`)
    }
  }

  run(['build', 'src/Tideline.Api', '-v', 'q', '--nologo'])
  run(['build', 'src/Tideline.Cli', '-v', 'q', '--nologo'])

  for (const f of [E2E_DB, `${E2E_DB}-wal`, `${E2E_DB}-shm`, `${E2E_DB.replace(/[^\\/]+$/, 'keys')}`]) fs.rmSync(f, { force: true, recursive: true })

  const fixtures = path.join(repo, 'tests', 'fixtures')
  cli('migrate')
  cli('setup', path.join(fixtures, 'household.json'))
  cli(
    'import',
    path.join(fixtures, 'truist', 'acct_1111_03_01_2026_to_03_31_2026.csv'),
    path.join(fixtures, 'truist', 'acct_3333_03_01_2026_to_03_31_2026.csv'),
    path.join(fixtures, 'truist', 'acct_2222_03_01_2026_to_03_31_2026.csv'),
  )

  // the owner: set up the way a real one is, from a link, by global-setup once the app is running
  const link = linkFrom(cli('user', 'add', '--email', OWNER_EMAIL, '--name', 'E2E Owner', '--role', 'owner', '--url', `http://localhost:${WEB_PORT}`))
  fs.writeFileSync(LINK_FILE, JSON.stringify({ url: link }))

  process.env.TIDELINE_E2E_READY = '1'
}
