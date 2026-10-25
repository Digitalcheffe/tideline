import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

/**
 * Build the API and CLI, then load the made-up March 2026 household into a fresh database.
 * Runs when the Playwright config loads, which is before the web servers start (they would lock the build output).
 */
export function prepare(db: string) {
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

  for (const f of [db, `${db}-wal`, `${db}-shm`]) fs.rmSync(f, { force: true })

  const cli = (...args: string[]) => run(['run', '--project', 'src/Tideline.Cli', '--no-build', '--', ...args, '--db', `Data Source=${db}`])
  const fixtures = path.join(repo, 'tests', 'fixtures')

  cli('migrate')
  cli('setup', path.join(fixtures, 'household.json'))
  cli(
    'import',
    path.join(fixtures, 'truist', 'acct_1111_03_01_2026_to_03_31_2026.csv'),
    path.join(fixtures, 'truist', 'acct_3333_03_01_2026_to_03_31_2026.csv'),
    path.join(fixtures, 'truist', 'acct_2222_03_01_2026_to_03_31_2026.csv'),
  )
  process.env.TIDELINE_E2E_READY = '1'
}
