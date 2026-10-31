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

  test('Run all the rules again shows what would change without changing it', async ({ page }) => {
    await page.goto('/settings/rules')
    await page.getByText('Run all the rules again over past lines').click()
    await page.getByLabel('Which lines').selectOption({ label: 'Everything' })
    await page.getByRole('button', { name: 'See what would change' }).click()
    await expect(page.getByRole('status').filter({ hasText: /lines? would change|Nothing would change/ })).toBeVisible()
  })
})
