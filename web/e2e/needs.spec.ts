import { expect, test, type Page } from '@playwright/test'
import { pickCategory } from './pick'

// Signed in as the owner by global-setup. The lines here are in a month no other spec uses.
async function api(page: Page, method: 'get' | 'post' | 'patch', url: string, data?: unknown) {
  const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
  const response = await page.request[method](url, { headers: { 'X-CSRF-Token': csrf }, data })
  expect(response.ok(), `${method} ${url}: ${await response.text()}`).toBeTruthy()
  return response.json()
}

/** A line nothing can place: it lands in Uncategorized, which is what "needs a category" means. */
async function addWaitingLine(page: Page, description: string, cents: number) {
  const accounts: { id: number; name: string }[] = await api(page, 'get', '/api/v1/accounts')
  const categories: { id: number; name: string }[] = await api(page, 'get', '/api/v1/categories')
  return (
    await api(page, 'post', '/api/v1/transactions', {
      accountId: accounts.find((a) => a.name === 'Checking')!.id,
      postedOn: '2025-10-05',
      amountCents: cents,
      description,
      categoryId: categories.find((c) => c.name === 'Uncategorized')!.id,
    })
  ).id as number
}

const waiting = async (page: Page): Promise<number> => (await api(page, 'get', '/api/v1/needs-category')).count

test.describe.serial('needs a category', () => {
  test('the Overview and the menu show how many lines are waiting, live, and it opens those lines', async ({ page }) => {
    await addWaitingLine(page, 'E2E MYSTERY SHOP 77', -2500)
    const count = await waiting(page)
    expect(count).toBeGreaterThan(0)

    await page.goto('/?from=2025-10-01&to=2025-10-31')
    const tile = page.getByRole('region', { name: 'Needs a category' })
    await expect(tile.getByRole('link', { name: new RegExp(`^${count} lines? needs? a category$`) })).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Screens' }).getByRole('link', { name: /Needs a category/ })).toContainText(String(count))

    // the number opens exactly those lines in the Ledger, with no dates in the way
    await tile.getByRole('link', { name: /need/ }).first().click()
    await expect(page).toHaveURL(/needs=true/)
    await expect(page.getByText('needs a category', { exact: true })).toBeVisible()
    await expect(page.getByText('E2E MYSTERY SHOP 77')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Make a rule' }).first()).toBeVisible()
  })

  test('answering a line takes it out of the pile and the count goes down at once', async ({ page }) => {
    const before = await waiting(page)

    await page.goto('/needs')
    const row = page.getByRole('row').filter({ hasText: 'E2E MYSTERY SHOP 77' })
    await pickCategory(page, row.getByRole('combobox', { name: 'Category for E2E MYSTERY SHOP 77' }), 'Groceries')
    await row.getByRole('button', { name: 'Answer', exact: true }).click()

    await expect(page.getByRole('row').filter({ hasText: 'E2E MYSTERY SHOP 77' })).toHaveCount(0) // the message under the filters may still name it
    expect(await waiting(page)).toBe(before - 1)
    await expect(page.getByRole('navigation', { name: 'Screens' }).getByRole('link', { name: /Needs a category/ })).toContainText(String(before - 1))

    // it is now Groceries, Required, set by hand, and no longer tagged review
    await page.goto('/lines?from=2025-10-01&to=2025-10-31&q=E2E+MYSTERY')
    await expect(page.getByRole('row', { name: /E2E MYSTERY SHOP 77/ })).toContainText('Groceries')
    await expect(page.getByRole('row', { name: /E2E MYSTERY SHOP 77/ })).not.toContainText('review')
  })

  test('an unnamed check raises its own alert, and answering it clears the alert at once', async ({ page }) => {
    await addWaitingLine(page, 'Check #9001', -25000)
    await api(page, 'post', '/api/v1/notify/evaluate')
    const openChecks = async () => (await api(page, 'get', '/api/v1/flags')).filter((f: { kind: string }) => f.kind === 'check_unknown')

    const flags = await openChecks()
    expect(flags.map((f: { title: string }) => f.title)).toContain('Check #9001, $250.00, Oct 5: what was it?')

    await page.goto('/needs')
    const row = page.getByRole('row').filter({ hasText: 'Check #9001' })
    await pickCategory(page, row.getByRole('combobox', { name: 'Category for Check #9001' }), 'Rent')
    await row.getByRole('button', { name: 'Answer', exact: true }).click()
    await expect(page.getByRole('row').filter({ hasText: 'Check #9001' })).toHaveCount(0)

    const after = await openChecks()
    expect(after.map((f: { title: string }) => f.title)).not.toContain('Check #9001, $250.00, Oct 5: what was it?')
  })

  test('"Remember as a rule" makes a rule from the words before the store number', async ({ page }) => {
    // lines added by hand are never changed by a rule, so this checks the rule that is made, not what it does to other lines
    await addWaitingLine(page, 'TWINSHOP ALPHA #1', -1000)
    const before = await waiting(page)

    await page.goto('/needs')
    const row = page.getByRole('row').filter({ hasText: 'TWINSHOP ALPHA #1' })
    await pickCategory(page, row.getByRole('combobox', { name: 'Category for TWINSHOP ALPHA #1' }), 'Groceries')
    await row.getByRole('button', { name: 'Answer and make a rule' }).click()

    await expect(page.getByRole('status').filter({ hasText: /made the rule/ })).toContainText('Lines like TWINSHOP ALPHA')
    expect(await waiting(page)).toBe(before - 1)

    await page.goto('/settings/rules')
    await expect(page.getByRole('cell', { name: 'Lines like TWINSHOP ALPHA', exact: true })).toBeVisible()
    await expect(page.getByText('the description contains TWINSHOP ALPHA')).toBeVisible()
  })

  test('a category that does not exist is made from the row, and a line can count as another type', async ({ page }) => {
    const trash = await addWaitingLine(page, 'E2E TRASH PICKUP 5', -3500)
    const cleaner = await addWaitingLine(page, 'E2E CLEANER 6', -9000)

    await page.goto('/needs')
    // no "E2E Trash Service" category yet: make it right here, as a bill
    let row = page.getByRole('row').filter({ hasText: 'E2E TRASH PICKUP 5' })
    const box = row.getByRole('combobox', { name: 'Category for E2E TRASH PICKUP 5' })
    await box.click()
    await box.fill('E2E Trash Service')
    await page.getByRole('option', { name: /Make a new category/ }).click()
    await page.getByLabel('Type for the new category').selectOption('bill')
    await page.getByRole('button', { name: 'Make it and use it' }).click()
    await expect(box).toHaveValue('E2E Trash Service')
    await row.getByRole('button', { name: 'Answer', exact: true }).click()
    await expect(page.getByRole('row').filter({ hasText: 'E2E TRASH PICKUP 5' })).toHaveCount(0)
    const made = await api(page, 'get', `/api/v1/transactions/${trash}`)
    expect(made.categoryName).toBe('E2E Trash Service')
    expect(made.type).toBe('bill')

    // Rent is a Required category; the check is for a house cleaner, which this household counts as Extra
    row = page.getByRole('row').filter({ hasText: 'E2E CLEANER 6' })
    await pickCategory(page, row.getByRole('combobox', { name: 'Category for E2E CLEANER 6' }), 'Rent')
    await row.getByLabel('Type for E2E CLEANER 6').selectOption({ label: 'Extra' })
    await row.getByRole('button', { name: 'Answer', exact: true }).click()
    await expect(page.getByRole('row').filter({ hasText: 'E2E CLEANER 6' })).toHaveCount(0)
    const counted = await api(page, 'get', `/api/v1/transactions/${cleaner}`)
    expect(counted.categoryName).toBe('Rent')
    expect(counted.type).toBe('extra')
  })
})
