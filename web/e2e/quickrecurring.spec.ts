import { expect, test, type Page } from '@playwright/test'

// Signed in as the owner by global-setup. The payees are unique to this spec so it disturbs nothing else.
async function api(page: Page, method: 'get' | 'post', url: string, data?: unknown) {
  const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
  const response = await page.request[method](url, { headers: { 'X-CSRF-Token': csrf }, data })
  expect(response.ok(), `${method} ${url}: ${await response.text()}`).toBeTruthy()
  return response.json()
}

function monthsAgo(n: number): string {
  const d = new Date()
  d.setDate(1)
  d.setMonth(d.getMonth() - n)
  d.setDate(11)
  return d.toISOString().slice(0, 10)
}

test.describe.serial('Track a bill from the ledger', () => {
  test('a line opens the quick form filled in, and confirming links the ticked lines', async ({ page }) => {
    const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
    const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
    const ids: number[] = []
    for (const n of [1, 2, 3]) {
      const created = await api(page, 'post', '/api/v1/transactions', {
        accountId: accounts.find((a) => a.name === 'Checking')!.id, postedOn: monthsAgo(n), amountCents: -6_450, description: 'E2E QUICK WATER 7731',
        payee: 'E2E Quick Water', categoryId: categories.find((c) => c.name === 'Groceries')!.id, type: 'extra', // not a bill, so the Bills totals other specs check stay as they were
      })
      ids.push(created.id)
    }

    await page.goto(`/settings/recurring?lines=${ids[0]}`)
    await expect(page.getByRole('region', { name: 'Track a bill or subscription' })).toBeVisible()
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue('E2E Quick Water')
    await expect(page.getByLabel('Usual amount')).toHaveValue('64.50')
    await expect(page.getByText(/^3 of \d+ ticked$/)).toBeVisible() // the three of this payee; other lines of the category are offered, not ticked

    await page.getByRole('button', { name: 'Add it and link 3 lines' }).click()
    await expect(page.getByText(/Added E2E Quick Water and linked 3 lines/)).toBeVisible()

    const items: { name: string; lineCount: number }[] = await api(page, 'get', '/api/v1/recurring')
    expect(items.find((i) => i.name === 'E2E Quick Water')?.lineCount).toBe(3)
  })

  test('the Bills grid offers Track this on an Other row', async ({ page }) => {
    const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
    const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
    // a bill line with no recurring item makes an Other row; it is left out again at the end so the totals other specs check stay as they were
    const line = await api(page, 'post', '/api/v1/transactions', {
      accountId: accounts.find((a) => a.name === 'Checking')!.id, postedOn: new Date().toISOString().slice(0, 10), amountCents: -1_234, description: 'E2E OTHER ROW',
      payee: 'E2E Other Row', categoryId: categories.find((c) => c.name === 'Utilities')!.id, type: 'bill',
    })
    try {
      await page.goto('/bills')
      const row = page.locator('tbody tr', { hasText: 'Other: Utilities' })
      await expect(row).toBeVisible()
      await row.getByRole('link', { name: 'Track this' }).click()
      await expect(page).toHaveURL(/\/settings\/recurring\?category=\d+/)
      await expect(page.getByRole('region', { name: 'Track a bill or subscription' })).toBeVisible()
      await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Utilities')
    } finally {
      await api(page, 'post', '/api/v1/transactions/leave-out', { ids: [line.id], reason: 'e2e cleanup' })
    }
  })
})
