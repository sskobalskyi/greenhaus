import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

const mock = 'http://localhost:8090'
const api = `${mock}/api/greenhaus/v1`

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mock}/mock/reset`)
  await page.addInitScript(() => {
    localStorage.setItem('locale', 'en')
    localStorage.removeItem('mockTier')
  })
})

const horizontalOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } })

  test('no page is wider than the screen, in either language', async ({ page }) => {
    for (const locale of ['en', 'uk']) {
      await page.addInitScript((value) => localStorage.setItem('locale', value), locale)
      await page.goto('/')
      await page.getByRole('link', { name: 'Kitchen herbs' }).first().waitFor()
      expect(await horizontalOverflow(page), `pots in ${locale}`).toBeLessThanOrEqual(1)
      await page.goto('/plans')
      await page.getByRole('heading', { level: 1 }).waitFor()
      expect(await horizontalOverflow(page), `plans in ${locale}`).toBeLessThanOrEqual(1)
      await page.goto('/')
      await page.getByRole('link', { name: 'Kitchen herbs' }).first().click()
      await page.waitForURL(/\/pots\//)
      for (const tab of ['controls', 'devices', 'assistant']) {
        await page.goto(page.url().replace(/\?.*$/, '') + `?tab=${tab}`)
        await expect(page.getByRole('tab', { selected: true })).toBeVisible()
        expect(await horizontalOverflow(page), `${tab} in ${locale}`).toBeLessThanOrEqual(1)
      }
    }
  })

  test('the tab that opens from a link is brought into view and the mode labels are not cut off', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).first().click()
    await page.goto(page.url().replace(/\?.*$/, '') + '?tab=assistant')
    const active = page.getByRole('tab', { name: 'Помічник', selected: true })
    await expect(active).toBeVisible()
    await expect(active).toBeInViewport()
    await page.goto(page.url().replace(/\?.*$/, '') + '?tab=controls')
    const longest = page.getByRole('radio', { name: 'Лише сповіщення' }).first()
    await expect(longest).toBeVisible()
    const box = await longest.boundingBox()
    // The label wraps inside its segment: the segment is taller than one line instead of the text being clipped.
    expect(box!.height).toBeGreaterThan(36)
    expect(await longest.evaluate((e) => e.scrollWidth <= e.clientWidth + 1)).toBe(true)
  })
})

test('the audit log speaks the language of the person and shows what came of each action', async ({ page, request }) => {
  const pots = (await (await request.get(`${api}/pots`)).json()) as { id: string; name: string }[]
  const pot = pots.find((p) => p.name === 'Kitchen herbs')!
  const actuators = (await (await request.get(`${api}/pots/${pot.id}/actuators`)).json()) as { id: string; mode: string }[]
  const pump = actuators.find((a) => a.id === 'pump') ?? actuators[0]
  await request.put(`${api}/pots/${pot.id}/actuators/${pump.id}/mode`, { data: { mode: pump.mode === 'notify' ? 'off' : 'notify' } })
  await request.post(`${api}/pots/${pot.id}/actuators/${pump.id}/run`, { data: { durationSeconds: 7 } })

  await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
  await page.goto(`/pots/${pot.id}?tab=controls`)
  await expect(page.getByText('Ви запустили на 7 с')).toBeVisible()
  await expect(page.getByText(/Режим змінено: /)).toBeVisible()
  // Nothing of the server's English is left in the log, and the outcome is shown.
  await expect(page.getByText(/requested by the user| -> /)).toHaveCount(0)
  await expect(page.getByText('Виконано').first()).toBeVisible()
})

test('plant species have names in the language of the person, and a degree sign is a degree sign', async ({ page, request }) => {
  await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
  const pots = (await (await request.get(`${api}/pots`)).json()) as { id: string; name: string }[]
  await page.goto(`/pots/${pots.find((p) => p.name === 'Kitchen herbs')!.id}?tab=plants`)
  await page.locator('main').getByRole('combobox').first().click()
  await expect(page.getByRole('option', { name: 'Базилік' })).toBeVisible()
  await expect(page.getByRole('option', { name: 'Basil' })).toHaveCount(0)
  await page.keyboard.press('Escape')

  await page.goto('/')
  await expect(page.getByText(/\d\s?°C/).first()).toBeVisible()
  expect(await page.getByText(/\d\sC\b/).count()).toBe(0)
})

test('a failed load offers a retry and stays accessible', async ({ page }) => {
  await page.route('**/api/greenhaus/v1/pots', (route) => route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'x', status: 500, code: 'boom' }) }))
  await page.goto('/')
  const alert = page.getByRole('alert')
  await expect(alert).toBeVisible()
  await expect(alert.getByRole('button', { name: 'Try again' })).toBeVisible()
  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')).toEqual([])
})