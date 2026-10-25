import { expect, test } from '@playwright/test'

// Numbers are worked out by hand from tests/fixtures/truist (March 2026).
const MARCH = 'from=2026-03-01&to=2026-03-31'

test.describe.serial('smoke', () => {
  test('Overview shows the spread and it adds up', async ({ page }) => {
    await page.goto(`/?${MARCH}`)

    const spread = page.locator('.spread')
    await expect(spread).toContainText('$2,000.00')
    await expect(spread).toContainText('$130.99')
    await expect(spread).toContainText('$585.40')
    await expect(spread).toContainText('$23.25')
    await expect(spread).toContainText('$300.00')
    await expect(spread).toContainText('$960.36')
    await expect(page.locator('.equation')).toContainText('In $2,000.00 = Bills $130.99 + Required $585.40 + Extra $23.25 + One-time $0.00 + Savings $300.00 + Left over $960.36')
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
  })

  test('splitting $10 of groceries moves it from Required to Extra and the total does not change', async ({ page }) => {
    await page.goto(`/lines?${MARCH}&q=grocery`)
    await page.getByText('GROCERY STORE #12').click()

    const drawer = page.getByRole('dialog', { name: 'Transaction' })
    await drawer.getByRole('button', { name: 'Add a split' }).click()
    await drawer.getByLabel('Split amount').fill('10.00')
    await drawer.getByLabel('Split category').selectOption({ label: 'Uncategorized' })
    await expect(drawer).toContainText('Remaining unassigned: -$75.40')
    await drawer.getByRole('button', { name: 'Save splits' }).click()
    await expect(drawer.getByText('Remaining unassigned').last()).toBeVisible()
    await expect(drawer).toContainText('Uncategorized (Extra)')
    await drawer.getByRole('button', { name: 'Close' }).click()

    await page.goto(`/?${MARCH}`)
    const spread = page.locator('.spread')
    await expect(spread).toContainText('$575.40') // Required, was $585.40
    await expect(spread).toContainText('$33.25') // Extra, was $23.25
    await expect(spread).toContainText('$960.36') // Left over, unchanged
  })

  test('the phone layout has no sideways scrolling on the Overview', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 })
    await page.goto(`/?${MARCH}`)
    await expect(page.locator('.spread')).toBeVisible()

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(1)
  })
})
