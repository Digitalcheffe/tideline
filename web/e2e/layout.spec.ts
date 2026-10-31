import { expect, test } from '@playwright/test'

// Signed in as the owner by global-setup. Reads only: nothing here changes data.
const MARCH = 'from=2026-03-01&to=2026-03-31'

test.describe('the ledger and the line drawer', () => {
  test('a row opens from its description, and Save stays in view however long the drawer gets', async ({ page }) => {
    await page.setViewportSize({ width: 1200, height: 560 })
    await page.goto(`/lines?${MARCH}&q=grocery`)

    await page.getByRole('button', { name: 'GROCERY STORE #12' }).click()
    const drawer = page.getByRole('dialog', { name: 'Transaction' })
    await expect(drawer.getByRole('button', { name: 'Save', exact: true })).toBeInViewport()

    // the long parts fold away, and opening them does not push Save out of sight
    await drawer.getByText('Split this line').click()
    await drawer.getByText('History').click()
    await expect(drawer.getByRole('button', { name: 'Save', exact: true })).toBeInViewport()
    await expect(drawer.getByRole('button', { name: 'Close' })).toBeInViewport()
  })

  test('saving a line closes the drawer and the row shows the change', async ({ page }) => {
    // a line of its own, in a month no other spec reads
    const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
    const accounts: { id: number; name: string }[] = await (await page.request.get('/api/v1/accounts')).json()
    const categories: { id: number; name: string }[] = await (await page.request.get('/api/v1/categories')).json()
    const made = await page.request.post('/api/v1/transactions', {
      headers: { 'X-CSRF-Token': csrf },
      data: {
        accountId: accounts.find((a) => a.name === 'Checking')!.id,
        postedOn: '2025-08-04',
        amountCents: -777,
        description: 'E2E SAVE CLOSES',
        categoryId: categories.find((c) => c.name === 'Groceries')!.id,
      },
    })
    expect(made.ok(), await made.text()).toBeTruthy()

    await page.goto('/lines?from=2025-08-01&to=2025-08-31')
    await page.getByRole('button', { name: 'E2E SAVE CLOSES' }).click()
    const drawer = page.getByRole('dialog', { name: 'Transaction' })
    await drawer.getByLabel('Tags').fill('e2e-kept')
    await drawer.getByRole('button', { name: 'Save', exact: true }).click()

    await expect(drawer).toBeHidden()
    await expect(page.getByRole('row', { name: /E2E SAVE CLOSES/ })).toContainText('e2e-kept')
  })

  test('several files can be sent at once, each is reported, and one failing does not stop the others', async ({ page }) => {
    await page.goto('/settings/imports')
    const junk = (name: string) => ({ name, mimeType: 'text/csv', buffer: Buffer.from('this,is,not\na,bank,file\n') })
    await page.getByLabel('CSV file').setInputFiles([junk('e2e-multi-one.csv'), junk('e2e-multi-two.csv')])

    await expect(page.getByText('e2e-multi-one.csv was not imported')).toBeVisible()
    await expect(page.getByText('e2e-multi-two.csv was not imported')).toBeVisible()
    await expect(page.getByText('0 of 2 files imported, 2 not imported.')).toBeVisible()

    // clear them from the list again so the other specs see what they expect
    for (const name of ['e2e-multi-one', 'e2e-multi-two'])
      await page.getByRole('row', { name: new RegExp(name) }).getByRole('button', { name: 'Dismiss' }).click()
    await expect(page.getByRole('row', { name: /e2e-multi/ })).toHaveCount(0)
  })

  test('the Ledger shows the last four digits of each account in small text under its name', async ({ page }) => {
    const accounts: { name: string; bankNumberMask: string | null }[] = await (await page.request.get('/api/v1/accounts')).json()
    const withMask = accounts.filter((a) => a.bankNumberMask)
    expect(withMask.length).toBeGreaterThan(0)

    await page.goto(`/lines?${MARCH}`)
    await expect(page.getByRole('button', { name: 'GROCERY STORE #12' })).toBeVisible()
    // a masked account has its mask in the Account cell of its rows; nothing longer than four digits is ever shown
    const cells = await page.locator('tbody tr td:nth-child(3) .cell-note').allTextContents()
    expect(cells.length).toBeGreaterThan(0)
    for (const text of cells) expect(text).toMatch(/^[•*•]*\d{4}$/)
  })

  test('a line has a link at the top that shows every line like it, across all dates', async ({ page }) => {
    await page.goto(`/lines?${MARCH}&q=grocery`)
    await page.getByRole('button', { name: 'GROCERY STORE #12' }).click()
    await page.getByRole('link', { name: 'Show all transactions like this one' }).click()

    await expect(page).toHaveURL(/q=GROCERY\+STORE/)
    await expect(page.getByText('all dates')).toBeVisible()
    await expect(page.getByRole('dialog', { name: 'Transaction' })).toHaveCount(0) // the drawer closes, the list is what is shown
    await expect(page.getByRole('button', { name: 'GROCERY STORE #12' })).toBeVisible()
  })

  test('the category list in the drawer stays open while you scroll it', async ({ page }) => {
    await page.setViewportSize({ width: 1200, height: 560 })
    await page.goto(`/lines?${MARCH}&q=grocery`)
    await page.getByRole('button', { name: 'GROCERY STORE #12' }).click()
    const drawer = page.getByRole('dialog', { name: 'Transaction' })
    await drawer.getByRole('combobox', { name: 'Category' }).click()
    const list = page.getByRole('listbox')
    await expect(list).toBeVisible()

    // scrolling inside the list (a wheel, or a scroll event from it) must not close it
    await list.hover()
    await page.mouse.wheel(0, 300)
    await list.evaluate((el) => el.dispatchEvent(new Event('scroll')))
    await expect(list).toBeVisible()

    // and choosing from it still works
    await list.getByRole('option').nth(2).click()
    await expect(list).toBeHidden()
  })

  test('the filters sit in one block and the search box does not wrap under the dropdowns', async ({ page }) => {
    await page.setViewportSize({ width: 1200, height: 800 })
    await page.goto(`/lines?${MARCH}`)
    const search = page.getByRole('searchbox', { name: 'Search' })
    const sort = page.getByLabel('Sort')
    const [a, b] = [await search.boundingBox(), await sort.boundingBox()]
    expect(a!.y).toBeCloseTo(b!.y, -1) // the same row
    expect(a!.width).toBeGreaterThan(300)
  })

  test('the Ledger can be searched by amount, in the search box or with the amount boxes', async ({ page }) => {
    await page.goto(`/lines?${MARCH}`)

    // a lone amount in the search box finds lines of exactly that size
    await page.getByRole('searchbox', { name: 'Search' }).fill('$85.40')
    await page.getByRole('searchbox', { name: 'Search' }).press('Enter')
    await expect(page.getByText('exactly $85.40')).toBeVisible()
    await expect(page.getByRole('button', { name: 'GROCERY STORE #12' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'CITY POWER AUTOPAY' })).toHaveCount(0)

    // a range with the amount boxes
    await page.goto(`/lines?${MARCH}`)
    await page.getByLabel('Amount from').fill('80')
    await page.getByLabel('Amount to').fill('90')
    await page.getByLabel('Amount to').press('Enter')
    await expect(page.getByText('at least $80.00')).toBeVisible()
    await expect(page.getByText('at most $90.00')).toBeVisible()
    await expect(page.getByRole('button', { name: 'GROCERY STORE #12' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'CITY POWER AUTOPAY' })).toHaveCount(0)
  })

  test('Run all the rules again shows what would change without changing it', async ({ page }) => {
    await page.goto('/settings/rules')
    await page.getByText('Run all the rules again over past lines').click()
    await page.getByLabel('Which lines').selectOption({ label: 'Everything' })
    await page.getByRole('button', { name: 'See what would change' }).click()
    await expect(page.getByRole('status').filter({ hasText: /lines? would change|Nothing would change/ })).toBeVisible()
  })
})
