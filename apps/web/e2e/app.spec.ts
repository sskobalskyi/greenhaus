import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

const mock = 'http://localhost:8090'

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mock}/mock/reset`)
  // Start in English, light theme and with the mock's default tier.
  await page.addInitScript(() => {
    localStorage.setItem('locale', 'en')
    localStorage.removeItem('mockTier')
  })
})

const asTier = (page: Page, tier: string) => page.addInitScript((value) => localStorage.setItem('mockTier', value), tier)

test.describe('pots overview', () => {
  test('shows every pot with its state', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'My pots' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Kitchen herbs' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Balcony bed' })).toContainText('Needs attention')
    await expect(page.getByRole('link', { name: 'Window sill' })).toContainText('No data yet')
  })

  test('a pot that needs attention names the zone, not an average', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByRole('link', { name: 'Balcony bed' })).toContainText(/Check Zone \d/)
  })

  test('creating a pot works, and the novice limit offers a higher plan', async ({ page }) => {
    await asTier(page, 'novice')
    await page.goto('/')
    // The demo user already has three pots, so a novice (2 pots) is over the limit at once.
    await page.getByRole('button', { name: 'Add a pot' }).click()
    await page.getByLabel('Name').fill('Office fern')
    await page.getByRole('button', { name: 'Create pot' }).click()
    const dialog = page.getByRole('dialog', { name: 'This needs a higher plan' })
    await expect(dialog).toBeVisible()
    await expect(dialog).toContainText('Gardener')
    await dialog.getByRole('link', { name: 'See plans' }).click()
    await expect(page).toHaveURL(/\/plans$/)
  })

  test('a gardener can add a pot', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('button', { name: 'Add a pot' }).click()
    await page.getByLabel('Name').fill('Office fern')
    await page.getByRole('button', { name: 'Create pot' }).click()
    await expect(page.getByRole('link', { name: 'Office fern' })).toBeVisible()
  })
})

test.describe('a pot in the zones view', () => {
  test('shows each zone on its own and lets you switch metric', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Balcony bed' }).click()
    await expect(page.getByRole('heading', { name: 'Balcony bed' })).toBeVisible()
    // It opens on the metric that needs attention in the worst zone.
    await expect(page.getByLabel(/Zone 2: pH 7\.\d/)).toBeVisible()
    await page.getByRole('radio', { name: 'Soil moisture' }).click()
    await expect(page.getByLabel(/Zone 3: Soil moisture 4\d/)).toBeVisible()
  })

  test('the Simple view needs a primary channel and is free, Zones is paid', async ({ page }) => {
    await asTier(page, 'novice')
    await page.goto('/')
    await page.getByRole('link', { name: 'Window sill' }).click()
    await page.getByRole('radio', { name: 'Zones' }).click()
    await expect(page.getByRole('dialog', { name: 'This needs a higher plan' })).toBeVisible()
  })
})

test.describe('controls', () => {
  test('a heater cannot be automated without a thermal cutoff', async ({ page }) => {
    await asTier(page, 'druid')
    await page.goto('/')
    await page.getByRole('link', { name: 'Balcony bed' }).click()
    await page.getByRole('tab', { name: 'Controls' }).click()
    const heater = page.locator('[data-slot="card"]', { hasText: 'Heater' })
    await heater.getByRole('radio', { name: 'Auto' }).click()
    await expect(page.getByRole('alert')).toContainText('thermal cutoff')
  })

  test('automation beyond the plan is locked, safety limits stay visible', async ({ page }) => {
    await asTier(page, 'novice')
    await page.goto('/')
    await page.getByRole('link', { name: 'Balcony bed' }).click()
    await page.getByRole('tab', { name: 'Controls' }).click()
    const heater = page.locator('[data-slot="card"]', { hasText: 'Heater' })
    await expect(heater.getByRole('radio', { name: 'Auto' })).toBeDisabled()
    await expect(heater).toContainText('per run')
  })

  test('the kill switch stops everything and is recorded', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).click()
    await page.getByRole('tab', { name: 'Controls' }).click()
    await page.getByRole('button', { name: 'Stop everything' }).click()
    await expect(page.getByText('Everything is stopped')).toBeVisible()
    await expect(page.getByRole('button', { name: /Run for/ }).first()).toBeDisabled()
    await expect(page.getByText('Kill switch turned on')).toBeVisible()
  })
})

test.describe('plants and care', () => {
  test('plants that disagree are flagged', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Window sill' }).click()
    await page.getByRole('tab', { name: 'Plants' }).click()
    await expect(page.getByText('These plants want different conditions')).toBeVisible()
    await expect(page.getByRole('note')).toContainText('soil moisture')
  })

  test('a care task can be marked as done', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).click()
    await page.getByRole('tab', { name: 'Care' }).click()
    await page.getByRole('button', { name: 'Mark as done' }).first().click()
    await expect(page.getByRole('heading', { name: 'Done' })).toBeVisible()
  })
})

test.describe('plans and claiming', () => {
  test('plans compare the three tiers and mark the current one', async ({ page }) => {
    await asTier(page, 'novice')
    await page.goto('/plans')
    for (const name of ['Novice', 'Gardener', 'Druid']) await expect(page.getByText(name, { exact: true }).first()).toBeVisible()
    await expect(page.getByRole('button', { name: 'Your plan' })).toBeDisabled()
    // Prices come from the billing mock, and only the free tier says Free.
    await expect(page.getByText('Free', { exact: true })).toHaveCount(1)
    await expect(page.getByText(/\$3\.00 per month/)).toBeVisible()
    await expect(page.getByText(/\$7\.00 per month/)).toBeVisible()
  })

  test('a purchase grant at risk is explained', async ({ page }) => {
    await page.route('**/me/entitlements', async (route) => {
      const response = await route.fetch({ headers: { ...route.request().headers(), 'x-mock-scenario': 'grant-at-risk' } })
      await route.fulfill({ response })
    })
    await page.goto('/plans')
    await expect(page.getByText('Your subscription is at risk')).toBeVisible()
  })

  test('scanning the QR code links a controller to a pot', async ({ page }) => {
    await page.goto('/claim?d=ghx-demo-0001&t=demo-token')
    await expect(page.getByText('Greenhaus Pot Controller P1')).toBeVisible()
    await page.getByLabel('Which pot should it go into?').click()
    await page.getByRole('option', { name: 'Window sill' }).click()
    await page.getByRole('button', { name: 'Add it to this pot' }).click()
    await expect(page.getByText('Your controller is linked.')).toBeVisible()
    await page.getByRole('link', { name: 'Open the pot' }).click()
    await expect(page.getByText('ghx-demo-0001')).toBeVisible()
  })

  test('a code from an order that has not shipped is refused', async ({ page }) => {
    await page.goto('/claim?d=ghx-demo-0002&t=demo-token-2')
    await expect(page.getByRole('alert')).toContainText('has not shipped yet')
  })

  test('opening the page without a code explains where to start', async ({ page }) => {
    await page.goto('/claim')
    await expect(page.getByText('Open this page from the QR code on the box.')).toBeVisible()
  })
})

test.describe('checkout', () => {
  test('choosing Druid subscribes and the plan changes everywhere', async ({ page }) => {
    await page.goto('/plans')
    await page.getByRole('button', { name: 'Choose Druid' }).click()
    await expect(page.getByRole('status')).toContainText('You are now on Druid')
    await expect(page.getByRole('button', { name: 'Your plan' })).toBeVisible()
    await expect(page.locator('header').getByText('Druid', { exact: true }).first()).toBeVisible()
  })

  test('a declined card leaves the plan unchanged', async ({ page }) => {
    await page.goto('/plans')
    await page.getByLabel('Simulate a declined card (demo)').check()
    await page.getByRole('button', { name: 'Choose Druid' }).click()
    await expect(page.getByRole('status')).toContainText('The card was declined')
    await expect(page.getByRole('button', { name: 'Choose Druid' })).toBeVisible()
  })

  test('choosing the free tier ends the paid plan at the end of the period', async ({ page }) => {
    await page.goto('/plans')
    await page.getByRole('button', { name: 'Choose Novice' }).click()
    await expect(page.getByText(/Your Gardener plan ends on/)).toBeVisible()
  })
  test('cancelling keeps the plan until the period ends and says when', async ({ page }) => {
    await page.goto('/plans')
    await page.getByRole('button', { name: 'Cancel subscription' }).click()
    await expect(page.getByText(/Your Gardener plan ends on/)).toBeVisible()
    await expect(page.getByRole('button', { name: 'Cancel subscription' })).toHaveCount(0)
  })
})

test.describe('editing a pot', () => {
  test('requirements can be changed and are kept', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).click()
    await page.getByRole('tab', { name: 'Requirements' }).click()
    const minimum = page.getByRole('slider', { name: 'Temperature, minimum' })
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled()
    const before = Number(await minimum.getAttribute('aria-valuenow'))
    await minimum.focus()
    await page.keyboard.press('ArrowRight')
    await page.keyboard.press('ArrowRight')
    await page.getByRole('button', { name: 'Save changes' }).click()
    await expect(page.getByRole('status')).toContainText('Saved')

    await page.reload()
    await page.getByRole('tab', { name: 'Requirements' }).click()
    await expect(page.getByRole('slider', { name: 'Temperature, minimum' })).toHaveAttribute('aria-valuenow', String(before + 1))
  })

  test('plants that disagree show up in the editor and the suggestion can be used', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Window sill' }).click()
    await page.getByRole('tab', { name: 'Requirements' }).click()
    await expect(page.getByRole('note')).toContainText('soil moisture')
    // The saved targets already are the suggestion, so changing one makes them differ and the suggestion brings them back.
    await page.getByRole('slider', { name: 'Temperature, minimum' }).focus()
    await page.keyboard.press('ArrowRight')
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeEnabled()
    await page.getByRole('button', { name: 'Use the suggestion from the plants' }).click()
    await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled()
  })

  test('automatic watering asks for calibration, then works', async ({ page }) => {
    await asTier(page, 'druid')
    await page.goto('/')
    await page.getByRole('link', { name: 'Balcony bed' }).click()
    await page.getByRole('tab', { name: 'Controls' }).click()
    const pump = page.locator('[data-slot="card"]', { hasText: 'Water pump' })
    await pump.getByRole('radio', { name: 'Auto' }).click()
    await expect(page.getByText('Calibrate before automating')).toBeVisible()

    await page.getByRole('button', { name: 'Calibrate sensors' }).click()
    await page.getByLabel('Raw value with the sensor dry, in the air').fill('3100')
    await page.getByLabel('Raw value with the sensor wet, in water').fill('1300')
    await page.getByRole('button', { name: 'Save calibration' }).click()
    await expect(page.getByText('The sensors are calibrated')).toBeVisible()

    await pump.getByRole('radio', { name: 'Auto' }).click()
    await expect(pump.getByRole('radio', { name: 'Auto' })).toBeChecked()
  })

  test('a custom care task can be added', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).click()
    await page.getByRole('tab', { name: 'Care' }).click()
    await page.getByLabel('What needs doing').fill('Wipe the leaves')
    await page.getByLabel('Due date').fill('2026-12-01')
    await page.getByRole('button', { name: 'Add task' }).click()
    await expect(page.getByText('Wipe the leaves')).toBeVisible()
  })

  test('a pot can be renamed and deleted', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Window sill' }).click()
    await page.getByRole('button', { name: 'Rename' }).click()
    await page.getByLabel('New name').fill('Bathroom ferns')
    await page.getByRole('button', { name: 'Save', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Bathroom ferns' })).toBeVisible()

    await page.getByRole('button', { name: 'Delete' }).click()
    await page.getByRole('button', { name: 'Delete this pot' }).click()
    await expect(page).toHaveURL(/\/$/)
    await expect(page.getByRole('link', { name: 'Bathroom ferns' })).toHaveCount(0)
  })
})
test.describe('language', () => {
  test('switches to Ukrainian, including the default zone names', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('radio', { name: 'Українська' }).click()
    await expect(page.getByRole('heading', { name: 'Мої горщики' })).toBeVisible()
    await page.getByRole('link', { name: 'Balcony bed' }).click()
    await page.getByRole('radio', { name: 'Вологість ґрунту' }).click()
    await expect(page.getByLabel(/Зона 3: Вологість ґрунту 4\d/)).toBeVisible()
    await expect(page.locator('html')).toHaveAttribute('lang', 'uk')
  })
})

test.describe('accessibility', () => {
  const pages: [string, string][] = [
    ['pots', '/'],
    ['plans', '/plans'],
    ['guide', '/guide'],
    ['claim', '/claim?d=ghx-demo-0001&t=demo-token'],
  ]
  for (const [name, path] of pages) {
    for (const [look, setup] of [
      ['light English', async () => {}],
      ['dark Ukrainian', async () => {}],
    ] as const) {
      test(`${name} page has no violations in ${look}`, async ({ page }) => {
        if (look === 'dark Ukrainian') {
          await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
        }
        await setup()
        await page.goto(path)
        await page.locator('main').waitFor()
        await page.waitForLoadState('networkidle')
        if (look === 'dark Ukrainian') await page.evaluate(() => document.documentElement.classList.add('dark'))
        const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
        expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([])
      })
    }
  }

  for (const tab of ['Requirements', 'Controls', 'Care']) {
    test(`the ${tab} tab has no violations`, async ({ page }) => {
      await page.goto('/')
      await page.getByRole('link', { name: 'Balcony bed' }).click()
      await page.getByRole('tab', { name: tab }).click()
      await page.waitForLoadState('networkidle')
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
      expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([])
    })
  }

  test('a pot page has no violations', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Balcony bed' }).click()
    await page.getByRole('heading', { name: 'Balcony bed' }).waitFor()
    await page.waitForLoadState('networkidle')
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([])
  })
})
