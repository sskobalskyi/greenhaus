import AxeBuilder from '@axe-core/playwright'
import { expect, type Page, test } from '@playwright/test'

const keycloak = /localhost:8181\/realms\/greenhaus/

/** Registers a new person in Keycloak's own form, and lands back in the app signed in. */
async function register(page: Page) {
  const username = `gardener${Date.now()}${Math.floor(Math.random() * 1000)}`
  await page.getByRole('link', { name: /register/i }).click()
  await page.locator('#firstName').fill('Test')
  await page.locator('#lastName').fill('Gardener')
  await page.locator('#email').fill(`${username}@example.com`)
  await page.locator('#username').fill(username)
  await page.locator('#password').fill('Passw0rd!x')
  await page.locator('#password-confirm').fill('Passw0rd!x')
  await page.getByRole('button', { name: /register/i }).click()
  await expect(page.getByTestId('signed-in-as')).toHaveText('Test Gardener')
}

test('a visitor reads the plans, with prices and limits, without an account and without a single 401', async ({ page }) => {
  const unauthorised: string[] = []
  page.on('response', (response) => {
    if (response.status() === 401) unauthorised.push(response.url())
  })

  await page.goto('/plans')
  await expect(page).toHaveURL(/localhost:5174\/plans/)
  await expect(page.getByRole('note').filter({ hasText: /create an account/i })).toBeVisible()
  await expect(page.getByText('Free', { exact: true })).toBeVisible()
  await expect(page.getByText(/\$3\.00 per month/)).toBeVisible()
  await expect(page.getByText(/\$7\.00 per month/)).toBeVisible()
  await expect(page.getByText('6 pots')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Your plan' })).toHaveCount(0)

  const accessibility = await new AxeBuilder({ page }).analyze()
  expect(accessibility.violations).toEqual([])
  expect(unauthorised).toEqual([])
})

test('"My pots" and choosing a plan send a visitor to Keycloak', async ({ page }) => {
  await page.goto('/plans')
  await page.getByRole('link', { name: 'My pots' }).click()
  await expect(page).toHaveURL(keycloak)

  await page.goto('/plans')
  await page.getByRole('button', { name: 'Choose Gardener' }).click()
  await expect(page).toHaveURL(keycloak)
})

test('a person registers, comes back signed in, and the API accepts the token', async ({ page }) => {
  await page.goto('/')
  await expect(page).toHaveURL(keycloak)
  await register(page)

  await expect(page).toHaveURL(/localhost:5174\/$/)
  // The tier badge comes from the API, so it only shows when the bearer token was accepted.
  await expect(page.locator('header').getByText('Novice', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Add a pot' }).click()
  await page.getByLabel('Name').fill('Window basil')
  await page.getByRole('button', { name: 'Create pot' }).click()
  await expect(page.getByText('Window basil').first()).toBeVisible()
})

test('the Keycloak pages speak the language of the app', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
  await page.goto('/')
  await expect(page).toHaveURL(keycloak)
  await expect(page.locator('html')).toHaveAttribute('lang', 'uk')
  await expect(page.getByRole('heading', { name: 'Увійдіть у свій обліковий запис' })).toBeVisible()
})

test('signing out ends the Keycloak session instead of signing the person straight back in', async ({ page }) => {
  await page.goto('/')
  await register(page)

  await page.getByRole('button', { name: 'Sign out' }).click()
  // Back at the login form, not at the callback: Keycloak no longer has a session to sign in with.
  await expect(page).toHaveURL(keycloak)
  await expect(page.locator('#username')).toBeVisible()
  await expect(page).not.toHaveURL(/auth\/callback/)

  // Opening the app again asks for credentials again.
  await page.goto('/')
  await expect(page).toHaveURL(keycloak)
  await expect(page.locator('#username')).toBeVisible()
})