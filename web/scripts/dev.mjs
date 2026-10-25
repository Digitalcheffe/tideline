// One command for local development: the API (restarts when C# changes) and the web app (reloads when TypeScript changes),
// on a database kept in data/dev/ at the repository root (ignored by git).
//
//   npm run dev:all             the app on your own data in data/dev/tideline.db
//   npm run dev:demo            the same, filled first with 5,000 made-up lines if the database does not exist yet
//   npm run dev:test            demo data with sign-in switched OFF, for testing on your own computer (a red banner shows)
//   TIDELINE_DB=C:/path/x.db npm run dev:all    a database somewhere else
//
// Nobody can use the app without signing in, so the first run makes an owner and prints their setup link. Set
// TIDELINE_OWNER_EMAIL and TIDELINE_OWNER_NAME before the first run to use your own details.
import { spawn, spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

const webDir = path.resolve(import.meta.dirname, '..')
const repo = path.resolve(webDir, '..')
const API_PORT = Number(process.env.API_PORT ?? 5130)
const WEB_PORT = Number(process.env.WEB_PORT ?? 5173)
const db = path.resolve(process.env.TIDELINE_DB ?? path.join(repo, 'data', 'dev', 'tideline.db'))
const connection = `Data Source=${db}`
const wantDemo = process.argv.includes('--demo')
const noAuth = process.argv.includes('--no-auth')

fs.mkdirSync(path.dirname(db), { recursive: true })

function dotnet(args, options = {}) {
  return spawnSync('dotnet', args, { cwd: repo, stdio: 'inherit', ...options })
}

function cli(...args) {
  return spawnSync('dotnet', ['run', '--project', 'src/Tideline.Cli', '--', ...args, '--db', connection], { cwd: repo, encoding: 'utf8' })
}

if (wantDemo && !fs.existsSync(db)) {
  console.log(`\nNo database yet, so loading 5,000 made-up lines into ${db}\n`)
  const files = path.join(path.dirname(db), 'demo-files')
  const r = dotnet(['run', '--project', 'src/Tideline.Cli', '--', 'demo', files, '--load', '--db', connection])
  if (r.status !== 0) process.exit(r.status ?? 1)
} else if (wantDemo) {
  console.log(`\n${db} already exists, so the demo data is not loaded again.\n`)
}

// first run: an owner, with a setup link. Lost it? `dotnet run --project src/Tideline.Cli -- user link --email <email>`
cli('migrate')
const people = noAuth ? { status: 1, stdout: '' } : cli('user', 'list') // with sign-in off nobody needs a setup link
if (people.status === 0 && people.stdout.trim().split(/\r?\n/).length <= 1) {
  const email = process.env.TIDELINE_OWNER_EMAIL ?? 'owner@example.com'
  const name = process.env.TIDELINE_OWNER_NAME ?? 'Owner'
  const added = cli('user', 'add', '--email', email, '--name', name, '--role', 'owner', '--url', `http://localhost:${WEB_PORT}`)
  const link = added.stdout.match(/https?:\/\/\S+\/setup\?token=\S+/)?.[0]
  console.log(`\nFirst run: made an owner (${email}). Open this link once to choose a password and set up an authenticator app:\n`)
  console.log(`  ${link ?? '(no link printed; run the user add command by hand and read its output)'}\n`)
}

const children = []

function run(name, command, args, options) {
  const child = spawn(command, args, { stdio: 'inherit', ...options })
  children.push(child)
  child.on('exit', (code) => {
    if (!stopping) {
      console.log(`\n${name} stopped (exit ${code}). Stopping the rest.`)
      stop(code ?? 1)
    }
  })
  return child
}

let stopping = false
function stop(code = 0) {
  if (stopping) return
  stopping = true
  for (const c of children) {
    if (c.pid === undefined || c.exitCode !== null) continue
    if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(c.pid), '/T', '/F'], { stdio: 'ignore' })
    else c.kill('SIGTERM')
  }
  process.exit(code)
}
process.on('SIGINT', () => stop(0))
process.on('SIGTERM', () => stop(0))

if (noAuth) console.log('\n  SIGN-IN IS SWITCHED OFF (--no-auth). Everyone on this computer is the owner. Testing only.\n')

run('The API', 'dotnet', ['watch', 'run', '--project', 'src/Tideline.Api', '--non-interactive', '--urls', `http://localhost:${API_PORT}`, '--', `--Database:ConnectionString=${connection}`, ...(noAuth ? ['--Tideline:DevNoAuth=true'] : [])], {
  cwd: repo,
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' },
})
run('The web app', process.execPath, [path.join(webDir, 'node_modules', 'vite', 'bin', 'vite.js')], {
  cwd: webDir,
  env: { ...process.env, API_PORT: String(API_PORT), WEB_PORT: String(WEB_PORT) },
})

// say when it is ready
const started = Date.now()
const timer = setInterval(async () => {
  try {
    const [api, web] = await Promise.all([fetch(`http://localhost:${API_PORT}/health`), fetch(`http://localhost:${WEB_PORT}/`)])
    if (api.ok && web.ok) {
      clearInterval(timer)
      console.log(`\n  Tideline is ready: http://localhost:${WEB_PORT}\n  Database: ${db}\n  Press Ctrl+C to stop.\n`)
    }
  } catch {
    if (Date.now() - started > 120_000) {
      clearInterval(timer)
      console.log('\nStill starting after two minutes; check the messages above.')
    }
  }
}, 1500)
