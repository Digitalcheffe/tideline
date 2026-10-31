import { expect, test, type Page } from '@playwright/test'

// Signed in as the owner by global-setup. The lines are in a month no other spec reads, and everything is taken away again at the end.
async function api(page: Page, method: 'get' | 'post' | 'delete', url: string, data?: unknown) {
  const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
  const response = await page.request[method](url, { headers: { 'X-CSRF-Token': csrf }, data })
  expect(response.ok(), `${method} ${url}: ${await response.text()}`).toBeTruthy()
  return response.json()
}

test('pairing two lines by hand can remember the pattern, and pairs the matching lines already there', async ({ page }) => {
  const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
  const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
  const checking = accounts.find((a) => a.name === 'Checking')!.id
  const savings = accounts.find((a) => a.name === 'Savings')!.id
  const groceries = categories.find((c) => c.name === 'Groceries')!.id
  const add = async (accountId: number, amountCents: number, description: string) =>
    (await api(page, 'post', '/api/v1/transactions', { accountId, postedOn: '2025-07-08', amountCents, description, categoryId: groceries, type: 'extra' })).id as number

  const ids = [
    await add(checking, -4100, 'ZZ SWEEPY OUT 1'),
    await add(savings, 4100, 'ZZ SWEEPY IN 1'),
    await add(checking, -6200, 'ZZ SWEEPY OUT 2'),
    await add(savings, 6200, 'ZZ SWEEPY IN 2'),
  ]
  let pattern: number | null = null

  try {
    await page.goto('/lines?from=2025-07-01&to=2025-07-31&q=ZZ+SWEEPY')
    await page.getByLabel('Select ZZ SWEEPY OUT 1').check()
    await page.getByLabel('Select ZZ SWEEPY IN 1').check()

    // the words come from the two lines, up to the number
    await page.getByLabel('Remember this for next time').check()
    await expect(page.getByLabel('Words on the line going out')).toHaveValue('ZZ SWEEPY OUT')
    await expect(page.getByLabel('Words on the line coming in')).toHaveValue('ZZ SWEEPY IN')
    await page.getByRole('button', { name: 'Pair as a transfer' }).click()

    await expect(page.getByRole('status').filter({ hasText: 'remembered for next time' })).toContainText('1 other pair in the ledger paired too')

    const patterns: { id: number; fromContains: string }[] = await api(page, 'get', '/api/v1/move-patterns')
    pattern = patterns.find((p) => p.fromContains === 'ZZ SWEEPY OUT')?.id ?? null
    expect(pattern).not.toBeNull()
    for (const id of ids) expect((await api(page, 'get', `/api/v1/transactions/${id}`)).moveId).not.toBeNull()
  } finally {
    if (pattern !== null) await api(page, 'delete', `/api/v1/move-patterns/${pattern}`)
    for (const id of ids) {
      const line = await (await page.request.get(`/api/v1/transactions/${id}`)).json()
      if (line.moveId) await page.request.delete(`/api/v1/moves/${line.moveId}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
    }
    for (const id of ids)
      await page.request.delete(`/api/v1/transactions/${id}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
  }
})

test('a transfer can start a pattern from its drawer, with both accounts and their words filled in', async ({ page }) => {
  const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
  const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
  const checking = accounts.find((a) => a.name === 'Checking')!.id
  const savings = accounts.find((a) => a.name === 'Savings')!.id
  const groceries = categories.find((c) => c.name === 'Groceries')!.id
  const add = async (accountId: number, amountCents: number, description: string) =>
    (await api(page, 'post', '/api/v1/transactions', { accountId, postedOn: '2025-07-09', amountCents, description, categoryId: groceries, type: 'extra' })).id as number
  const out = await add(checking, -3300, 'ZZ MOVER OUT 7')
  const inn = await add(savings, 3300, 'ZZ MOVER IN 7')
  const move = (await api(page, 'post', '/api/v1/moves', { transactionIds: [out, inn] })).id as number

  try {
    await page.goto(`/lines?from=2025-07-01&to=2025-07-31&move=include&tx=${out}`)
    await page.getByRole('link', { name: 'Make a pattern from this transfer' }).click()
    await expect(page).toHaveURL(/\/settings\/accounts\?pattern=1/)
    await expect(page.getByLabel('Words on that line').first()).toHaveValue('ZZ MOVER OUT')
    await expect(page.getByLabel('Words on that line').last()).toHaveValue('ZZ MOVER IN')
  } finally {
    await page.request.delete(`/api/v1/moves/${move}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
    for (const id of [out, inn])
      await page.request.delete(`/api/v1/transactions/${id}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
  }
})
