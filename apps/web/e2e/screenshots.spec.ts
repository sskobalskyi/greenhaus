import { test, type Page } from '@playwright/test'
import path from 'node:path'

// Captures the README screenshots. Run it on purpose:  SCREENSHOTS=1 npx playwright test screenshots
test.skip(!process.env.SCREENSHOTS, 'only when SCREENSHOTS=1')

const out = path.resolve(import.meta.dirname, '..', '..', '..', 'docs', 'screenshots', 'app')

const looks = [
  { name: 'light-en', locale: 'en', dark: false },
  { name: 'dark-uk', locale: 'uk', dark: true },
]

// A null tier lets billing decide, which is what a real user sees.
async function open(page: Page, url: string, look: (typeof looks)[number], tier: string | null = 'gardener') {
  await page.addInitScript(
    ([locale, mockTier]) => {
      localStorage.setItem('locale', locale)
      if (mockTier) localStorage.setItem('mockTier', mockTier)
      else localStorage.removeItem('mockTier')
    },
    [look.locale, tier],
  )
  await page.goto(url)
  await page.locator('main').waitFor()
  await page.waitForLoadState('networkidle')
  await page.evaluate((dark) => document.documentElement.classList.toggle('dark', dark), look.dark)
  await page.evaluate(() => document.fonts.ready)
  await page.waitForTimeout(400)
}

test.beforeEach(async ({ request }) => {
  await request.post('http://localhost:8090/mock/reset')
})

for (const look of looks) {
  test(`pots ${look.name}`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    await open(page, '/', look)
    await page.screenshot({ path: path.join(out, `pots-${look.name}.png`), fullPage: true })
  })

  test(`pot zones ${look.name}`, async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 900 })
    await open(page, '/', look)
    await page.locator('a', { hasText: 'Balcony bed' }).click()
    await page.locator('h1').waitFor()
    await page.waitForLoadState('networkidle')
    await page.waitForTimeout(400)
    await page.screenshot({ path: path.join(out, `pot-zones-${look.name}.png`), fullPage: true })
  })

  test(`controls ${look.name}`, async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 900 })
    await open(page, '/', look, 'novice')
    await page.locator('a', { hasText: 'Balcony bed' }).click()
    await page.locator('[role="tab"]').nth(3).click()
    await page.waitForLoadState('networkidle')
    await page.waitForTimeout(400)
    await page.screenshot({ path: path.join(out, `controls-${look.name}.png`), fullPage: true })
  })

  test(`requirements ${look.name}`, async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 900 })
    await open(page, '/', look)
    await page.locator('a', { hasText: 'Window sill' }).click()
    await page.locator('[role="tab"]').nth(2).click()
    await page.waitForLoadState('networkidle')
    await page.waitForTimeout(400)
    await page.screenshot({ path: path.join(out, `requirements-${look.name}.png`), fullPage: true })
  })

  test(`plans ${look.name}`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 })
    await open(page, '/plans', look, null)
    await page.screenshot({ path: path.join(out, `plans-${look.name}.png`), fullPage: true })
  })

  test(`claim ${look.name}`, async ({ page }) => {
    await page.setViewportSize({ width: 430, height: 900 })
    await open(page, '/claim?d=ghx-demo-0001&t=demo-token', look)
    await page.screenshot({ path: path.join(out, `claim-${look.name}.png`), fullPage: true })
  })
}
