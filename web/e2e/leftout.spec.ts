import { expect, test, type Page } from '@playwright/test'

// Signed in as the owner by global-setup. Lines are in a month no other spec uses.
async function api(page: Page, method: 'get' | 'post' | 'patch', url: string, data?: unknown) {
  const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
  const response = await page.request[method](url, { headers: { 'X-CSRF-Token': csrf }, data })
  expect(response.ok(), `${method} ${url}: ${await response.text()}`).toBeTruthy()
  return response.json()
}

async function addLine(page: Page, description: string, cents: number) {
  const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
  const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
  return (
    await api(page, 'post', '/api/v1/transactions', {
      accountId: accounts.find((a) => a.name === 'Checking')!.id, postedOn: '2025-09-14', amountCents: cents, description, categoryId: categories[0].id, type: 'extra',
    })
  ).id as number
}

test.describe.serial('leaving a line out, and deleting one', () => {
  test('a line can be left out with a reason, found under Left out, and brought back', async ({ page }) => {
    const line = await addLine(page, 'E2E DOUBLE CHARGE', -4321)

    await page.goto(`/lines?from=2025-09-01&to=2025-09-30&tx=${line}`)
    await page.getByLabel('Why leave this line out').fill('charged twice')
    await page.getByRole('button', { name: 'Leave out of the totals' }).click()
    await expect(page.getByText('Left out of the totals').first()).toBeVisible()
    await expect(page.getByText(/charged twice/)).toBeVisible()
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toHaveCount(0)

    // it is gone from the normal list and listed under Left out
    await page.getByRole('button', { name: 'Close' }).click()
    await expect(page.getByText('E2E DOUBLE CHARGE')).toHaveCount(0)
    await page.getByLabel('Left out', { exact: true }).click()
    await expect(page.getByLabel('Left out', { exact: true })).toBeChecked()
    await expect(page.getByText('E2E DOUBLE CHARGE')).toBeVisible()

    // select it and bring it back
    await page.getByLabel('Select E2E DOUBLE CHARGE').check()
    await page.getByRole('button', { name: 'Bring back into the totals' }).click()
    await expect(page.getByText('Nothing is left out.')).toBeVisible()
    await page.getByLabel('Left out', { exact: true }).click()
    await expect(page.getByLabel('Left out', { exact: true })).not.toBeChecked()
    await expect(page.getByText('E2E DOUBLE CHARGE')).toBeVisible()
  })

  test('a line added by hand can be deleted from its drawer', async ({ page }) => {
    const line = await addLine(page, 'E2E ADDED BY MISTAKE', -999)

    await page.goto(`/lines?from=2025-09-01&to=2025-09-30&tx=${line}`)
    page.once('dialog', (d) => void d.accept())
    await page.getByRole('button', { name: 'Delete this line' }).click()
    await expect(page.getByText('Transaction', { exact: true })).toBeVisible()
    await expect.poll(async () => (await page.request.get(`/api/v1/transactions/${line}`)).status()).toBe(404)
  })
})
