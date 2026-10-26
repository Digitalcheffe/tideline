import { expect, test } from '@playwright/test'

// Signed in as the owner by global-setup.
test.describe.serial('options that start from compose and can be changed in the app', () => {
  test('sign-in timing is changed in Settings, applies, and goes back to the compose value', async ({ page }) => {
    await page.goto('/settings/sessions')
    await expect(page.getByRole('heading', { name: 'Sign-in timing and limits' })).toBeVisible()

    const idle = page.getByLabel(/Sign out after being idle/)
    await expect(idle).toHaveValue('12')
    await expect(page.getByText('The compose file says 12 hours.').first()).toBeVisible()

    await idle.fill('5')
    const saved = page.waitForResponse((r) => r.url().includes('/api/v1/settings/app') && r.request().method() === 'PUT')
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).ok()).toBeTruthy()
    await expect(page.getByText('changed here')).toBeVisible()
    await expect(page.getByLabel(/Sign out after being idle/)).toHaveValue('5')

    // a number that makes no sense is explained in words
    await page.getByLabel(/Wrong tries before an account is locked/).fill('2')
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('use a number from 3 to 20')

    await page.reload()
    await page.getByRole('button', { name: 'Use the compose value' }).click()
    await expect(page.getByText('changed here')).toHaveCount(0)
    await expect(page.getByLabel(/Sign out after being idle/)).toHaveValue('12')
  })

  test('the inbox folder has its settings on the Imports tab', async ({ page }) => {
    await page.goto('/settings/imports')
    await expect(page.getByRole('heading', { name: 'Inbox folder setting' })).toBeVisible()
    await expect(page.getByLabel(/How often the inbox is looked at/)).toHaveValue('300')
  })
})
