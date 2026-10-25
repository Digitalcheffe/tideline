import { defineConfig } from '@playwright/test'
import { prepare } from './e2e/prepare'
import { API_PORT, E2E_DB, STATE_FILE, WEB_PORT } from './e2e/helpers'

// A throwaway database, loaded from the made-up fixtures by e2e/prepare.ts, behind the real API and the real web app.
prepare()

export default defineConfig({
  testDir: "e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  globalSetup: './e2e/global-setup.ts',
  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    // locally: PW_CHANNEL=msedge (or chrome) uses the browser already installed; CI installs Playwright's chromium
    channel: process.env.PW_CHANNEL || undefined,
    trace: 'retain-on-failure',
    // everyone starts signed in as the owner, unless a test says otherwise
    storageState: STATE_FILE,
  },
  webServer: [
    {
      // the test pages are plain http, so the cookie is not marked Secure here (the API tests check the real attributes)
      command: `dotnet run --project ../src/Tideline.Api --no-build --urls http://localhost:${API_PORT} -- "--Database:ConnectionString=Data Source=${E2E_DB}" --Tideline:SecureCookie=false --Tideline:AuthRequestsPerMinute=1000`,
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
