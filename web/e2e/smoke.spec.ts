import { expect, test } from '@playwright/test'

// Numbers are worked out by hand from tests/fixtures/truist (March 2026).
const MARCH = 'from=2026-03-01&to=2026-03-31'

test.describe.serial('smoke', () => {
  test('Overview shows the spread and it adds up', async ({ page }) => {
    await page.goto(`/?${MARCH}`)

    const spread = page.locator('.spread')
    // pay 2,000.00 plus a 1.25 deposit no rule knows, which counts as income until a person decides; extra is the 20.00 and 4.50 spent
    await expect(spread).toContainText('$2,001.25')
    await expect(spread).toContainText('$130.99')
    await expect(spread).toContainText('$585.40')
    await expect(spread).toContainText('$24.50')
    await expect(spread).toContainText('$300.00')
    await expect(spread).toContainText('$960.36')
    await expect(page.locator('.equation')).toContainText('In $2,001.25 = Bills $130.99 + Required $585.40 + Extra $24.50 + One-time $0.00 + Savings $300.00 + Left over $960.36')
  })

  test('a number drills to the lines behind it, and a line opens in the drawer', async ({ page }) => {
    await page.goto(`/?${MARCH}`)

    await page.locator('.spread-item', { hasText: 'Bills' }).getByRole('link').click()
    await expect(page).toHaveURL(/\/lines\?.*type=bill/)
    await expect(page.getByText('2 lines')).toBeVisible()
    await expect(page.getByText('CITY POWER AUTOPAY')).toBeVisible()
    await expect(page.getByText('SPOTIFY USA')).toBeVisible()

    await page.getByText('CITY POWER AUTOPAY').click()
    const drawer = page.getByRole('dialog', { name: 'Transaction' })
    await expect(drawer).toContainText('From the bank')
    await expect(drawer).toContainText('rule Bill: power')
    await drawer.getByRole('button', { name: 'Close' }).click()
    await expect(drawer).toBeHidden()
  })

  test('the Bills grid totals add up and match the Overview', async ({ page }) => {
    await page.goto('/bills?from=2026-01-01&to=2026-12-31')

    const grid = page.locator('table').first()
    await expect(grid).toContainText('City Power')
    await expect(grid).toContainText('Spotify')
    await expect(grid.locator('tfoot')).toContainText('$130.99 paid')
    await expect(grid.locator('tbody tr', { hasText: 'City Power' })).toContainText('Paid')
    await expect(grid.locator('tbody tr', { hasText: 'City Power' })).toContainText('Missing')

    // the year's Total and the Average a month stay in view on the right, without scrolling sideways
    await expect(grid.getByRole('columnheader', { name: 'Total', exact: true })).toBeInViewport()
    await expect(grid.getByRole('columnheader', { name: 'Avg a month' })).toBeInViewport()
  })

  test('splitting $10 of groceries moves it from Required to Extra and the total does not change', async ({ page }) => {
    await page.goto(`/lines?${MARCH}&q=grocery`)
    await page.getByText('GROCERY STORE #12').click()

    const drawer = page.getByRole('dialog', { name: 'Transaction' })
    await drawer.getByText('Split this line').click() // the split editor folds away until it is wanted
    await drawer.getByRole('button', { name: 'Add a split' }).click()
    await drawer.getByLabel('Split amount').fill('10.00')
    await drawer.getByLabel('Split category').selectOption({ label: 'Uncategorized' })
    await expect(drawer).toContainText('Remaining unassigned: -$75.40')
    await drawer.getByRole('button', { name: 'Save splits' }).click()
    await expect(drawer.getByText('Remaining unassigned').last()).toBeVisible()
    await expect(drawer.getByLabel('Split category')).toHaveCount(1) // the saved split is shown, and its type came from the category
    await expect(drawer.getByLabel('Split type')).toHaveValue('extra')
    await drawer.getByRole('button', { name: 'Close' }).click()

    await page.goto(`/?${MARCH}`)
    const spread = page.locator('.spread')
    await expect(spread).toContainText('$575.40') // Required, was $585.40
    await expect(spread).toContainText('$34.50') // Extra, was $24.50
    await expect(spread).toContainText('$960.36') // Left over, unchanged
  })

  test('the menu is on the left on a wide screen and keeps the chosen period when you move between screens', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 })
    await page.goto(`/?${MARCH}`)

    const menu = page.getByRole('complementary', { name: 'Menu' })
    const box = await menu.boundingBox()
    expect(box!.x).toBe(0)
    expect(box!.width).toBeLessThan(300)
    await expect(menu.getByRole('link', { name: 'Bills' })).toBeVisible()
    await expect(page.getByLabel('Month', { exact: true })).toHaveValue('2026-03')

    await menu.getByRole('link', { name: 'Required' }).click()
    await expect(page).toHaveURL(/\/required\?from=2026-03-01&to=2026-03-31/)
    await expect(page.getByRole('heading', { name: 'Required', level: 1 })).toBeVisible()
  })

  test('on a phone the menu opens from the button and closes when you pick a screen', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 })
    await page.goto(`/?${MARCH}`)
    const menu = page.getByRole('complementary', { name: 'Menu' })
    await expect(menu).toBeHidden()

    await page.getByRole('button', { name: 'Menu' }).click()
    await expect(menu).toBeVisible()
    await menu.getByRole('link', { name: 'Income' }).click()

    await expect(page.getByRole('heading', { name: /^Income/ })).toBeVisible()
    await expect(menu).toBeHidden()
  })

  test('the Ledger has its own tab, shows the period in the menu, and filters by account', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 })
    await page.goto(`/?${MARCH}`)

    await page.getByRole('complementary', { name: 'Menu' }).getByRole('link', { name: 'Ledger' }).click()
    await expect(page.getByRole('heading', { name: 'Ledger', level: 1 })).toBeVisible()

    const count = page.getByRole('heading', { level: 2 }).filter({ hasText: /^\d+ lines?$/ })
    await expect(count).toBeVisible()
    const all = await count.textContent()
    await expect(page.getByText('PAYROLL ACME CORP')).toBeVisible()

    await page.getByLabel('Account', { exact: true }).selectOption({ label: 'Card' })
    await expect(page.getByText('GROCERY STORE #12').first()).toBeVisible()
    await expect(page.getByText('PAYROLL ACME CORP')).toBeHidden()
    expect(await count.textContent()).not.toBe(all)

    await page.getByRole('button', { name: 'Clear filters' }).click()
    await expect(page.getByText('PAYROLL ACME CORP')).toBeVisible()
    await expect(count).toHaveText(all!)
  })

  test('in the Ledger, clicking a tag filters by it and several lines can be fixed at once', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 })
    await page.goto(`/lines?${MARCH}`)

    await page.getByRole('button', { name: 'review', exact: true }).first().click()
    await expect(page).toHaveURL(/tag=review/)
    await expect(page.getByText('MYSTERY VENDOR 9')).toBeVisible()

    await page.getByLabel('Select all lines on this page').check()
    await expect(page.getByText(/\d+ selected/)).toBeVisible()
    await page.getByLabel('Add tags').fill('checked')
    await page.getByLabel('Remove tags').fill('review')
    await page.getByRole('button', { name: 'Apply' }).click()

    await expect(page.getByText('No lines match.')).toBeVisible()
    await page.goto(`/lines?${MARCH}&tag=checked`)
    await expect(page.getByText('MYSTERY VENDOR 9')).toBeVisible()
  })

  test('two lines are paired as a transfer by hand and unpaired from the drawer', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 })
    const accounts: { id: number; name: string }[] = await (await page.request.get('/api/v1/accounts')).json()
    const categories: { id: number; name: string }[] = await (await page.request.get('/api/v1/categories')).json()
    const csrf: string = (await (await page.request.get('/api/v1/auth/me')).json()).csrf
    const add = async (account: string, cents: number, description: string) =>
      page.request.post('/api/v1/transactions', {
        headers: { 'X-CSRF-Token': csrf },
        data: { accountId: accounts.find((a) => a.name === account)!.id, postedOn: '2026-03-22', amountCents: cents, description, categoryId: categories[0].id, type: 'extra' },
      })
    const added: number[] = []
    for (const [account, cents, description] of [['Checking', -7700, 'PAIR ME OUT'], ['Savings', 7700, 'PAIR ME IN']] as const) {
      const response = await add(account, cents, description)
      expect(response.ok(), await response.text()).toBeTruthy()
      added.push((await response.json()).id)
    }

    await page.goto(`/lines?${MARCH}&q=PAIR ME`)
    await page.getByLabel('Select all lines on this page').check()
    await page.getByLabel('Remember this for next time').uncheck() // it starts ticked; this is a one-off pairing, so no pattern is saved
    await page.getByRole('button', { name: 'Pair as a transfer' }).click()
    await expect(page.getByText('No lines match.')).toBeVisible()

    await page.goto(`/lines?${MARCH}&q=PAIR ME&move=include`)
    await page.getByText('PAIR ME OUT').click()
    await page.getByRole('button', { name: 'Not a transfer: unpair' }).click()
    await expect(page.getByText('Everything on this account that day')).toBeVisible()
    await expect(page.getByText('Set by')).toBeVisible()

    // lines added by hand move the account balances the Imports page checks against the bank: take them away again
    for (const id of added) await page.request.delete(`/api/v1/transactions/${id}`, { headers: { 'X-CSRF-Token': csrf } })
  })

  test('Help explains the app one topic at a time, from the menu', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 })
    await page.goto(`/?${MARCH}`)
    await page.getByRole('link', { name: 'Help' }).click()
    await expect(page).toHaveURL(/\/help\/start$/)
    await expect(page.getByRole('heading', { name: 'Help' })).toBeVisible()
    await expect(page.getByText('Every number on every screen is a link')).toBeVisible()

    await page.getByRole('navigation', { name: 'Help topics' }).getByRole('link', { name: 'Importing bank files' }).click()
    await expect(page).toHaveURL(/\/help\/import$/)
    await expect(page.getByText('Bank lines can never be edited or deleted')).toBeVisible()
    // the guides link to the places they talk about
    await page.getByRole('link', { name: 'Settings, Imports' }).click()
    await expect(page).toHaveURL(/\/settings\/imports/)
  })

  test('the phone layout has no sideways scrolling on the Overview', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 })
    await page.goto(`/?${MARCH}`)
    await expect(page.locator('.spread')).toBeVisible()

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(1)
  })
})
