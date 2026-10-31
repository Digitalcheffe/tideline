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

    // the words start as the whole text of each line; the number is deleted so the next transfer matches too
    await expect(page.getByLabel('Remember this for next time')).toBeChecked() // an owner is asked to remember it unless they say otherwise
    await expect(page.getByLabel('Words on the line going out')).toHaveValue('ZZ SWEEPY OUT 1')
    await expect(page.getByLabel('Words on the line coming in')).toHaveValue('ZZ SWEEPY IN 1')
    await page.getByLabel('Words on the line going out').fill('ZZ SWEEPY OUT')
    await page.getByLabel('Words on the line coming in').fill('ZZ SWEEPY IN')
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

test('a plain pairing says where the lines went and can be undone', async ({ page }) => {
  const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
  const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
  const groceries = categories.find((c) => c.name === 'Groceries')!.id
  const add = async (accountName: string, amountCents: number, description: string) =>
    (await api(page, 'post', '/api/v1/transactions', {
      accountId: accounts.find((a) => a.name === accountName)!.id, postedOn: '2025-07-10', amountCents, description, categoryId: groceries, type: 'extra',
    })).id as number
  const ids = [await add('Checking', -2800, 'ZZ UNDOME OUT 3'), await add('Savings', 2800, 'ZZ UNDOME IN 3')]

  try {
    await page.goto('/lines?from=2025-07-01&to=2025-07-31&q=ZZ+UNDOME')
    await page.getByLabel('Select all lines on this page').check()
    await page.getByLabel('Remember this for next time').uncheck()
    // leaving lines out is done from a line's own drawer, not from this bar
    await expect(page.getByRole('button', { name: 'Leave out of the totals' })).toHaveCount(0)
    await page.getByRole('button', { name: 'Pair as a transfer' }).click()

    // the pair is left out of the list, and the message says so and offers the way back
    await expect(page.getByText('No lines match.')).toBeVisible()
    await expect(page.getByRole('status').filter({ hasText: 'Paired as a transfer' })).toContainText('left out of this list')
    await page.getByRole('button', { name: 'Undo' }).click()
    await expect(page.getByRole('status').filter({ hasText: 'Unpaired' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'ZZ UNDOME OUT 3' })).toBeVisible()
  } finally {
    for (const id of ids) {
      const line = await (await page.request.get(`/api/v1/transactions/${id}`)).json()
      const csrf = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
      if (line.moveId) await page.request.delete(`/api/v1/moves/${line.moveId}`, { headers: { 'X-CSRF-Token': csrf } })
      await page.request.delete(`/api/v1/transactions/${id}`, { headers: { 'X-CSRF-Token': csrf } })
    }
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
    await expect(page.getByLabel('Words on that line').first()).toHaveValue('ZZ MOVER OUT 7')
    await expect(page.getByLabel('Words on that line').last()).toHaveValue('ZZ MOVER IN 7')
  } finally {
    await page.request.delete(`/api/v1/moves/${move}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
    for (const id of [out, inn])
      await page.request.delete(`/api/v1/transactions/${id}`, { headers: { 'X-CSRF-Token': (await (await page.request.get('/api/v1/auth/me')).json()).csrf } })
  }
})
