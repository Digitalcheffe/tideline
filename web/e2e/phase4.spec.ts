import { expect, test } from '@playwright/test'

// Signed in as the owner by global-setup. Names are unique so these do not disturb the other specs.
test.describe.serial('phase 4', () => {
  test('retirement says what to fill in, then gives the four numbers once it is filled in', async ({ page }) => {
    await page.goto('/retirement')
    await expect(page.getByText('Projection, not advice')).toBeVisible()
    await expect(page.getByText('Enter the yearly income you want in retirement')).toBeVisible()

    // a retirement account, in Settings
    await page.goto('/settings/accounts')
    await page.getByRole('button', { name: 'Add an account' }).click()
    await page.getByLabel('Name', { exact: true }).fill('E2E 401k')
    await page.locator('label', { hasText: /^KindChecking/ }).locator('select').selectOption('retirement')
    await page.getByLabel('You put in each month').fill('1000')
    await page.getByLabel('Your employer puts in each month').fill('500')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /E2E 401k/ })).toBeVisible()

    await page.goto('/retirement')
    await page.getByRole('button', { name: 'Add a person' }).click()
    await page.getByLabel('Name').fill('E2E Person')
    const born = new Date()
    born.setFullYear(born.getFullYear() - 39)
    await page.getByLabel('Date of birth').fill(born.toISOString().slice(0, 10))
    await page.getByLabel('Age to retire').fill('65')
    await page.getByLabel(/Social Security a year/).fill('40000')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /E2E Person/ })).toBeVisible()

    await page.getByRole('row', { name: /E2E 401k/ }).getByRole('button', { name: 'Update balance' }).click()
    await page.getByLabel('What it is worth').fill('100000')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /\$100,000\.00/ })).toBeVisible()

  })

  test('the goal is set and the four numbers appear', async ({ page }) => {
    await page.goto('/retirement')
    await page.getByRole('heading', { name: 'Goal and assumptions' }).locator('xpath=ancestor::section').getByRole('button', { name: 'Change' }).click()
    await page.getByLabel(/Income wanted in retirement/).fill('80000')
    await page.getByRole('button', { name: 'Save' }).click()

    await expect(page.getByText('Required at retirement')).toBeVisible()
    await expect(page.getByText('Projected', { exact: true })).toBeVisible()
    await expect(page.getByText(/Shortfall, in today|Surplus, in today/)).toBeVisible()
    await expect(page.getByText('Extra per month to close it')).toBeVisible()
    await expect(page.getByText('Projected balance by year')).toBeVisible()
  })

  test('a loan and the thing it is secured by give equity, a countdown and a what-if slider', async ({ page }) => {
    await page.goto('/settings/accounts')
    await page.getByRole('button', { name: 'Add an account' }).click()
    await page.getByLabel('Name', { exact: true }).fill('E2E Truck')
    await page.locator('label', { hasText: /^KindChecking/ }).locator('select').selectOption('asset')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /E2E Truck/ })).toBeVisible()

    await page.getByRole('button', { name: 'Add an account' }).click()
    await page.getByLabel('Name', { exact: true }).fill('E2E Truck loan')
    await page.locator('label', { hasText: /^KindChecking/ }).locator('select').selectOption('loan')
    await page.getByLabel('Interest rate, % a year').fill('5')
    await page.getByLabel(/Monthly payment, principal/).fill('569')
    await page.getByLabel('Secured by (for equity)').selectOption({ label: 'E2E Truck' })
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByRole('cell', { name: /E2E Truck loan/ })).toBeVisible()

    await page.goto('/?tab=networth')
    await page.getByRole('button', { name: 'Enter a figure' }).first().click()
    await page.getByLabel('Which one').selectOption({ label: 'E2E Truck (Asset)' })
    await page.getByLabel('What it is worth').fill('30000')
    await page.getByRole('button', { name: 'Save' }).click()
    await page.getByRole('row', { name: /E2E Truck loan|E2E Truck/ }).first().waitFor({ state: 'detached' }).catch(() => undefined)

    await page.getByRole('button', { name: 'Enter a figure' }).first().click()
    await page.getByLabel('Which one').selectOption({ label: 'E2E Truck loan (Loan)' })
    await page.getByLabel('What is owed').fill('20000')
    await page.getByRole('button', { name: 'Save' }).click()

    await expect(page.getByRole('heading', { name: 'Equity' })).toBeVisible()
    await expect(page.getByRole('cell', { name: '$10,000.00' }).first()).toBeVisible()
    await expect(page.getByText(/38\.1 months/)).toBeVisible()
    await expect(page.getByText(/\$1,670\.38/)).toBeVisible()

    await page.getByLabel('Extra per month').fill('100')
    await expect(page.getByRole('status').filter({ hasText: /sooner/ })).toContainText('$266.09')
  })

  test('a plan is made on the Planning screen and shows on the Overview', async ({ page }) => {
    await page.goto('/planning')
    await page.getByRole('button', { name: 'Make a plan' }).click()
    await page.getByLabel('Name').fill('E2E Wood stove')
    await page.getByLabel('Target amount').fill('1200')
    const next = new Date()
    next.setFullYear(next.getFullYear() + 1)
    await page.getByLabel('Be done by').fill(next.toISOString().slice(0, 10))
    await page.getByRole('button', { name: 'Save' }).click()

    await expect(page.getByText('E2E Wood stove').first()).toBeVisible()
    await page.goto('/')
    await expect(page.getByRole('link', { name: 'E2E Wood stove' })).toBeVisible()
  })

  test('the budget takes a guideline and fill from averages only proposes', async ({ page }) => {
    await page.goto('/budget?from=2026-01-01&to=2026-12-31')
    await expect(page.getByRole('heading', { name: /Budget, 2026/ })).toBeVisible()
    await page.getByRole('button', { name: 'Edit the budget' }).click()
    await page.getByLabel('Groceries guideline').fill('600')
    await page.getByRole('button', { name: 'Save the budget' }).click()
    await expect(page.getByRole('row', { name: /Groceries/ })).toContainText('$600.00 a month')

    await page.getByRole('button', { name: /Fill from last year/ }).first().click()
    await expect(page.getByRole('status').filter({ hasText: 'Nothing is kept until you do' })).toBeVisible()
    await page.getByRole('button', { name: 'Cancel' }).click()
    await expect(page.getByRole('row', { name: /Groceries/ })).toContainText('$600.00 a month')
  })

  test('savings lists the accounts and the plans', async ({ page }) => {
    await page.goto('/savings')
    await expect(page.getByRole('heading', { name: 'Savings', level: 1 })).toBeVisible()
    await expect(page.getByText('Going in each month').first()).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Plans this money is for' })).toBeVisible()
  })
})
