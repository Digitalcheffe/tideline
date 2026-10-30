import { expect, test, type Page } from '@playwright/test'

// Signed in as the owner by global-setup. Names are unique so these do not disturb the other specs.
async function api(page: Page, method: 'get' | 'post' | 'patch', url: string, data?: unknown) {
  const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
  const response = await page.request[method](url, { headers: { 'X-CSRF-Token': csrf }, data })
  expect(response.ok(), `${method} ${url}: ${await response.text()}`).toBeTruthy()
  return response.json()
}

test.describe.serial('phase 6', () => {
  test('an asset shows what it is worth, what is owed and what has been put into it', async ({ page }) => {
    const house = (await api(page, 'post', '/api/v1/accounts', { name: 'E2E House', kind: 'asset' })).id
    await api(page, 'post', `/api/v1/accounts/${house}/snapshots`, { asOn: '2026-03-01', balanceCents: 30_000_000, source: 'statement' })

    const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
    const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
    const line = (
      await api(page, 'post', '/api/v1/transactions', {
        accountId: accounts.find((a) => a.name === 'Checking')!.id, postedOn: '2025-11-18', amountCents: -123_456, description: 'E2E ROOF', categoryId: categories[0].id, type: 'extra',
      })
    ).id

    // the line is linked from its drawer
    await page.goto(`/lines?from=2025-11-01&to=2025-11-30&tx=${line}`)
    await page.getByLabel('Asset this was spent on').selectOption({ label: 'E2E House' })
    const saved = page.waitForResponse((r) => r.url().includes(`/api/v1/transactions/${line}`) && r.request().method() === 'PATCH')
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await saved).ok()).toBeTruthy()

    await page.goto('/assets')
    await expect(page.getByRole('heading', { name: 'E2E House' })).toBeVisible()
    await expect(page.getByText('$300,000.00').first()).toBeVisible()
    await expect(page.getByRole('link', { name: '$1,234.56' }).first()).toBeVisible()

    // the put-in number opens the line behind it
    await page.getByRole('link', { name: '$1,234.56' }).first().click()
    await expect(page.getByText('E2E ROOF')).toBeVisible()
    await expect(page.getByText(/spent on E2E House/)).toBeVisible()

    // a line added by hand moves the account's balance, which the Imports page checks against the bank: take it away again
    await page.request.delete(`/api/v1/transactions/${line}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
  })

  test('suggestions appear for what is flagged, can be marked reviewed, and each can be switched off', async ({ page }) => {
    await api(page, 'post', '/api/v1/notify/evaluate')
    await page.goto('/recommendations')
    await expect(page.getByRole('heading', { name: 'Recommendations' })).toBeVisible()

    const reviewed = page.getByRole('button', { name: 'Reviewed' })
    await expect(reviewed.first()).toBeVisible()
    const before = await reviewed.count()
    await reviewed.first().click()
    await expect(reviewed).toHaveCount(before - 1)

    // the suggestions ship with the app: there is no adding or editing, only switches
    await expect(page.getByRole('button', { name: 'Add a suggestion' })).toHaveCount(0)
    const first = page.getByLabel('Use Lines are waiting for review')
    await expect(first).toBeChecked()
    await first.click()
    await expect(first).not.toBeChecked()
    await first.click()
    await expect(first).toBeChecked()
  })
})
