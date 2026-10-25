import { expect, test } from '@playwright/test'

const background = (page: import('@playwright/test').Page) => page.evaluate(() => getComputedStyle(document.body).backgroundColor)

test.describe('theme', () => {
  test.use({ colorScheme: 'light' })

  test('Auto follows the device, Dark and Light override it, and the choice is remembered', async ({ page }) => {
    await page.goto('/')
    const group = page.getByRole('group', { name: 'Colour theme' })
    await expect(group.getByRole('button', { name: 'Auto' })).toHaveAttribute('aria-pressed', 'true')
    const light = await background(page)

    await group.getByRole('button', { name: 'Dark' }).click()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
    expect(await background(page)).not.toBe(light)

    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
    await expect(group.getByRole('button', { name: 'Dark' })).toHaveAttribute('aria-pressed', 'true')

    await group.getByRole('button', { name: 'Light' }).click()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
    expect(await background(page)).toBe(light)

    await group.getByRole('button', { name: 'Auto' }).click()
    await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/)
    await page.reload()
    await expect(group.getByRole('button', { name: 'Auto' })).toHaveAttribute('aria-pressed', 'true')
  })

  test('Auto goes dark when the device is dark, and a Light choice still wins', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' })
    await page.goto('/')
    const dark = await background(page)

    await page.getByRole('group', { name: 'Colour theme' }).getByRole('button', { name: 'Light' }).click()
    expect(await background(page)).not.toBe(dark)
  })
})
