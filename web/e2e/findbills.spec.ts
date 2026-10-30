import { expect, test, type Page } from '@playwright/test'

// Signed in as the owner by global-setup. The payee is unique to this spec so it disturbs nothing else.
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
  d.setDate(8)
  return d.toISOString().slice(0, 10)
}

test.describe.serial('Find my bills', () => {
  test('a payee that repeats is found, tracked with one click, and its lines are linked', async ({ page }) => {
    const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
    const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
    for (const n of [1, 2, 3, 4]) {
      await api(page, 'post', '/api/v1/transactions', {
        accountId: accounts.find((a) => a.name === 'Checking')!.id, postedOn: monthsAgo(n), amountCents: -2_999, description: 'E2E FITNESS CLUB 4471',
        payee: 'E2E Fitness Club', categoryId: categories.find((c) => c.name === 'Groceries')!.id, type: 'extra',
      })
    }

    await page.goto('/settings/recurring')
    await expect(page.getByRole('heading', { name: 'Found in your lines' })).toBeVisible()
    await expect(page.getByLabel('Track E2E Fitness Club')).toBeChecked()
    await page.getByLabel('Name for E2E Fitness Club').fill('E2E Gym')
    await page.getByLabel(/^Track (?!E2E)/).evaluateAll((boxes) => boxes.forEach((b) => (b as HTMLInputElement).checked && (b as HTMLInputElement).click())) // only this one
    await page.getByRole('button', { name: /^Track \d+ selected$/ }).click()

    await expect(page.getByText(/Added 1 item and linked 4 lines/)).toBeVisible()
    await expect(page.getByRole('cell', { name: 'E2E Gym' })).toBeVisible()
    const items: { name: string; lineCount: number }[] = await api(page, 'get', '/api/v1/recurring')
    expect(items.find((i) => i.name === 'E2E Gym')?.lineCount).toBe(4)
  })
})
