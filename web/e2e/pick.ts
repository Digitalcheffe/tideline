import { type Locator, type Page } from '@playwright/test'

/** Choose a category in the type-ahead picker: type its name, then take the option that starts with it. */
export async function pickCategory(page: Page, box: Locator, name: string) {
  await box.click()
  await box.fill(name)
  await page.getByRole('option', { name: new RegExp(`^${name}`) }).first().click()
}
