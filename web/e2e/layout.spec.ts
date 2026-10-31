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
