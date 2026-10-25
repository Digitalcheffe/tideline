import { expect, test } from '@playwright/test'

// Signed in as the owner by global-setup. Names are unique so these do not disturb the other specs.
test.describe.serial('settings', () => {
  test('the tabs are there and money accounts list the household', async ({ page }) => {
    await page.goto('/settings')
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
    await expect(page).toHaveURL(/\/settings\/accounts/)
    for (const tab of ['Money accounts', 'Categories and tags', 'Recurring', 'Rules', 'People', 'Sessions', 'API tokens', 'Logs'])
      await expect(page.getByRole('link', { name: tab })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Money accounts' })).toBeVisible()
    await expect(page.getByRole('cell', { name: /Checking/ }).first()).toBeVisible()
  })

  test('a category is added, renamed and shows up in the audit log', async ({ page }) => {
    await page.goto('/settings/categories')
    await page.getByRole('button', { name: 'Add a category' }).click()
    await page.getByLabel('Name').fill('E2E Pets')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /E2E Pets/ })).toBeVisible()

    await page.getByRole('row', { name: /E2E Pets/ }).getByRole('button', { name: 'Change' }).click()
    await page.getByLabel('Name').fill('E2E Pet care')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /E2E Pet care/ })).toBeVisible()

    await page.goto('/settings/logs')
    await page.getByLabel('Action').fill('category.updated')
    await page.getByRole('row', { name: /category\.updated/ }).first().click()
    await expect(page.getByText('E2E Pet care').first()).toBeVisible()
  })

  test('a rule is tried before it is saved, and refused when it has no conditions that make sense', async ({ page }) => {
    await page.goto('/settings/rules')
    await page.getByRole('button', { name: 'Add a rule' }).click()
    await page.getByLabel('Name').fill('E2E try this')
    await page.getByLabel('Value').fill('MYSTERY')
    await page.locator('label', { hasText: /^Category/ }).locator('select').selectOption({ label: 'Groceries' })
    await page.getByRole('button', { name: 'See what it would change' }).click()
    await expect(page.getByRole('status').filter({ hasText: 'would change' })).toBeVisible()

    await page.getByLabel('Value').fill('')
    await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
    await page.getByLabel('Value').fill('MYSTERY')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: 'E2E try this', exact: true })).toBeVisible()
    await expect(page.getByText('the description contains MYSTERY')).toBeVisible()
  })

  test('inviting someone gives a setup link once, and a token is shown once', async ({ page }) => {
    await page.goto('/settings/people')
    await page.getByRole('button', { name: 'Invite someone' }).click()
    await page.getByLabel('Email').fill('e2e-guest@example.com')
    await page.getByLabel('Name').fill('E2E Guest')
    await page.getByLabel('Role').selectOption('viewer')
    await page.getByRole('button', { name: 'Create and get the setup link' }).click()
    await expect(page.getByRole('textbox', { name: 'Setup link' })).toHaveValue(/\/setup\?token=/)

    await page.goto('/settings/tokens')
    await page.getByRole('button', { name: 'Make a token' }).click()
    await page.getByLabel('What will use it').fill('E2E script')
    await page.getByRole('button', { name: 'Make it' }).click()
    await expect(page.getByLabel('New token')).toHaveValue(/^tdl_/)
    await page.reload()
    await expect(page.getByRole('cell', { name: /E2E script/ })).toBeVisible()
    await expect(page.getByLabel('New token')).toBeHidden()
  })

  test('sessions lists this one', async ({ page }) => {
    await page.goto('/settings/sessions')
    await expect(page.getByText('this one')).toBeVisible()
  })
})
