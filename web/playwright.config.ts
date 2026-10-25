import os from 'node:os'
import path from 'node:path'
import { defineConfig } from '@playwright/test'
import { prepare } from './e2e/prepare'

// A throwaway database, loaded from the made-up fixtures by e2e/prepare.ts, behind the real API and the real web app.
const API_PORT = 5190
const WEB_PORT = 5191
const E2E_DB = path.join(os.tmpdir(), 'tideline-e2e.db')

prepare(E2E_DB)

export default defineConfig({
  testDir: 'e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    // locally: PW_CHANNEL=msedge (or chrome) uses the browser already installed; CI installs Playwright's chromium
    channel: process.env.PW_CHANNEL || undefined,
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: `dotnet run --project ../src/Tideline.Api --no-build --urls http://localhost:${API_PORT} -- "--Database:ConnectionString=Data Source=${E2E_DB}"`,
      url: `http://localhost:${API_PORT}/health`,
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: 'npm run dev',
      url: `http://localhost:${WEB_PORT}`,
      env: { API_PORT: String(API_PORT), WEB_PORT: String(WEB_PORT) },
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
})
