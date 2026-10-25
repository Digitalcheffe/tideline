import { expect, test } from '@playwright/test'
import { PASSWORD, WEB_PORT, cli, linkFrom } from './helpers'

// These start signed out. A virtual authenticator stands in for the phone or security key (Chromium's own test feature); it lives as long as the page.
test.use({ storageState: { cookies: [], origins: [] } })

test('a passkey is set up instead of an authenticator app, signs in after the password, and is managed from Your sign-in', async ({ page }) => {
  const cdp = await page.context().newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  const device = {
    options: { protocol: 'ctap2' as const, transport: 'internal' as const, hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  }
  const { authenticatorId: firstDevice } = await cdp.send('WebAuthn.addVirtualAuthenticator', device)

  const email = 'passkey.person@example.com'
  const link = linkFrom(cli('user', 'add', '--email', email, '--name', 'Passkey Person', '--role', 'member', '--url', `http://localhost:${WEB_PORT}`))
  await page.goto(link)
  await page.getByLabel('Password', { exact: false }).first().fill(PASSWORD)
  await page.getByLabel('Again').fill(PASSWORD)
  await page.getByRole('button', { name: 'Continue' }).click()

  // the first passkey is the second step, and comes with recovery codes
  await expect(page.getByRole('heading', { name: 'Set up your authenticator' })).toBeVisible()
  await page.getByRole('button', { name: 'Set up a passkey' }).click()
  await expect(page.getByRole('heading', { name: 'Save your recovery codes' })).toBeVisible()
  await expect(page.locator('.codes li')).toHaveCount(10)
  await page.getByRole('button', { name: 'I have saved them' }).click()
  await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()

  // signing out and in again: the password, then the passkey
  await page.getByRole('button', { name: 'Sign out' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Continue' }).click()
  await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible()
  await page.goto('/accounts') // the password alone opens nothing
  await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible()
  await page.getByRole('button', { name: 'Use a passkey' }).click()
  await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()

  // Your sign-in: the only way in cannot be removed; a second passkey can be added and then one removed
  await page.getByRole('link', { name: 'Your sign-in' }).click()
  await expect(page.getByRole('heading', { name: 'Your sign-in' })).toBeVisible()
  await expect(page.getByRole('row', { name: /^Passkey / })).toBeVisible()
  page.on('dialog', (d) => d.accept())
  await page.getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByRole('alert')).toContainText('only way to sign in')

  // a device that already has a passkey for Tideline is not asked to make another, so the second one comes from another device
  await cdp.send('WebAuthn.removeVirtualAuthenticator', { authenticatorId: firstDevice })
  await cdp.send('WebAuthn.addVirtualAuthenticator', device)
  await page.getByLabel(/Name for the new passkey/).fill('Laptop')
  await page.getByRole('button', { name: 'Add a passkey' }).click()
  await expect(page.getByRole('row', { name: /^Laptop / })).toBeVisible()
  await page.getByRole('row', { name: /^Laptop / }).getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByRole('row', { name: /^Laptop / })).toBeHidden()
})
