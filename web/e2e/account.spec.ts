import { expect, test } from '@playwright/test'
import { PASSWORD, WEB_PORT, cli, linkFrom, totp } from './helpers'

// These start signed out and use their own person, so the owner's password and authenticator are never touched.
test.use({ storageState: { cookies: [], origins: [] } })

const step = (offset = 0) => Math.floor(Date.now() / 30000) + offset

test('a person manages their own account from their name: name, password, a new phone, recovery codes and devices', async ({ page }) => {
  const email = 'account.person@example.com'
  const link = linkFrom(cli('user', 'add', '--email', email, '--name', 'Account Person', '--role', 'member', '--url', `http://localhost:${WEB_PORT}`))

  // set up like a new person: password, then the authenticator
  await page.goto(link)
  await page.getByLabel('Password', { exact: false }).first().fill(PASSWORD)
  await page.getByLabel('Again').fill(PASSWORD)
  await page.getByRole('button', { name: 'Continue' }).click()
  await page.getByText('Cannot scan?').click()
  const firstSecret = ((await page.locator('code.secret').textContent()) ?? '').replace(/\s/g, '')
  await page.getByLabel(/6 digit code/).fill(totp(firstSecret, step()))
  await page.getByRole('button', { name: 'Turn it on' }).click()
  await page.getByRole('button', { name: 'I have saved them' }).click()
  await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()

  // click the name at the bottom left
  await page.getByRole('link', { name: /Account Person/ }).click()
  await expect(page).toHaveURL(/\/account$/)
  await expect(page.getByRole('heading', { name: 'Your account' })).toBeVisible()

  // name
  await page.getByLabel('Name', { exact: true }).fill('Renamed Person')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('link', { name: /Renamed Person/ })).toBeVisible()

  // password
  const newPassword = 'a brand new long passphrase'
  await page.getByLabel('Current password').fill(PASSWORD)
  await page.getByLabel(/^New password \(at least/).fill(newPassword)
  await page.getByLabel('New password again').fill(newPassword)
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText('Password changed')).toBeVisible()

  // a new phone: prove it with the old code, scan the new one, switch
  await page.getByRole('button', { name: 'Set up a new phone' }).click()
  await page.getByLabel(/code from your current authenticator/).fill(totp(firstSecret, step(1))) // the next code, so the one used a moment ago is not reused
  await page.getByRole('button', { name: 'Continue' }).click()
  await page.getByText('Cannot scan?').click()
  const secondSecret = ((await page.locator('code.secret').textContent()) ?? '').replace(/\s/g, '')
  expect(secondSecret).not.toBe(firstSecret)
  await page.getByLabel(/6 digit code it shows/).fill(totp(secondSecret, step()))
  await page.getByRole('button', { name: 'Switch to the new phone' }).click()
  await expect(page.locator('.codes li')).toHaveCount(10)
  await page.getByRole('button', { name: 'I have saved them' }).click()

  // recovery codes and devices
  await expect(page.getByText(/You have\s*10\s*unused codes/)).toBeVisible()
  await expect(page.getByText('this device')).toBeVisible()

  // sign out and back in with the new password and the new phone only
  await page.getByRole('button', { name: 'Sign out' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(newPassword)
  await page.getByRole('button', { name: 'Continue' }).click()
  await page.getByLabel('Code').fill(totp(secondSecret, step(1)))
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()
  await expect(page.getByRole('link', { name: /Renamed Person/ })).toBeVisible()
})
