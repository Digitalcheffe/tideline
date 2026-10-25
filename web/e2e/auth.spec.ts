import { expect, test } from '@playwright/test'
import { PASSWORD, WEB_PORT, cli, freshCode, linkFrom, readAuth, totp, writeAuth } from './helpers'

// These start signed out. The other specs start signed in as the owner.
test.use({ storageState: { cookies: [], origins: [] } })

test.describe.serial('signing in', () => {
  test('the app shows nothing until you sign in', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
    await expect(page.getByText('Where the money went')).toBeHidden()
  })

  test('a wrong password is refused and says so without saying which part was wrong', async ({ page }) => {
    const a = readAuth()
    await page.goto('/')
    await page.getByLabel('Email').fill(a.email)
    await page.getByLabel('Password').fill('not the right one at all')
    await page.getByRole('button', { name: 'Continue' }).click()
    await expect(page.getByRole('alert')).toContainText('Wrong email or password')
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  })

  test('password, then the authenticator code, then the app; and a wrong code does not get in', async ({ page }) => {
    const a = readAuth()
    await page.goto('/')
    await page.getByLabel('Email').fill(a.email)
    await page.getByLabel('Password').fill(a.password)
    await page.getByRole('button', { name: 'Continue' }).click()

    await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible()
    await page.goto('/accounts') // the password alone opens nothing
    await expect(page.getByRole('heading', { name: 'Second step' })).toBeVisible()

    await page.getByLabel('Code').fill('000000')
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toContainText('did not match')

    const { code, step } = await freshCode(a.secret, a.lastStep)
    writeAuth({ ...a, lastStep: step })
    await page.getByLabel('Code').fill(code)
    await page.getByRole('button', { name: 'Sign in' }).click()

    await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()
    await expect(page.getByText('E2E Owner')).toBeVisible()
  })

  test('a recovery code signs you in once and then it is spent', async ({ page }) => {
    const a = readAuth()
    const login = async () => {
      await page.goto('/')
      await page.getByLabel('Email').fill(a.email)
      await page.getByLabel('Password').fill(a.password)
      await page.getByRole('button', { name: 'Continue' }).click()
      await page.getByRole('button', { name: /recovery code/ }).click()
    }

    await login()
    await page.getByLabel('Recovery code').fill(a.recoveryCodes[0])
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()

    await page.context().clearCookies()
    await login()
    await page.getByLabel('Recovery code').fill(a.recoveryCodes[0])
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toContainText('did not match')
  })

  test('signing out puts the sign-in page back', async ({ page }) => {
    const a = readAuth()
    await page.goto('/')
    await page.getByLabel('Email').fill(a.email)
    await page.getByLabel('Password').fill(a.password)
    await page.getByRole('button', { name: 'Continue' }).click()
    await page.getByRole('button', { name: /recovery code/ }).click()
    await page.getByLabel('Recovery code').fill(a.recoveryCodes[1])
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()

    await page.getByRole('button', { name: 'Sign out' }).click()
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
    await page.goto('/bills')
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  })

  test('a new person follows their setup link: password, authenticator, recovery codes, then in', async ({ page }) => {
    const link = linkFrom(cli('user', 'add', '--email', 'new.person@example.com', '--name', 'New Person', '--role', 'member', '--url', `http://localhost:${WEB_PORT}`))
    await page.goto(link)

    await expect(page.getByRole('heading', { name: 'Welcome, New Person' })).toBeVisible()
    await page.getByLabel('Password', { exact: false }).first().fill('password1234')
    await page.getByLabel('Again').fill('password1234')
    await page.getByRole('button', { name: 'Continue' }).click()
    await expect(page.getByRole('alert')).toContainText('common')

    await page.getByLabel('Password', { exact: false }).first().fill(PASSWORD)
    await page.getByLabel('Again').fill(PASSWORD)
    await page.getByRole('button', { name: 'Continue' }).click()

    await expect(page.getByRole('heading', { name: 'Set up your authenticator' })).toBeVisible()
    await expect(page.getByLabel('QR code for your authenticator app')).toBeVisible()
    await page.getByText('Cannot scan?').click()
    const secret = ((await page.locator('code.secret').textContent()) ?? '').replace(/\s/g, '')
    expect(secret).toMatch(/^[A-Z2-7]{32}$/)

    await page.getByLabel(/6 digit code/).fill(totp(secret, Math.floor(Date.now() / 30000)))
    await page.getByRole('button', { name: 'Turn it on' }).click()

    await expect(page.getByRole('heading', { name: 'Save your recovery codes' })).toBeVisible()
    await expect(page.locator('.codes li')).toHaveCount(10)
    await page.getByRole('button', { name: 'I have saved them' }).click()

    await expect(page.getByRole('link', { name: 'Overview' })).toBeVisible()
    await expect(page.getByText('New Person')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Settings' })).toBeHidden() // members do not manage the household

    // the link is spent
    await page.context().clearCookies()
    await page.goto(link)
    await expect(page.getByRole('heading', { name: 'This link does not work' })).toBeVisible()
  })
})
