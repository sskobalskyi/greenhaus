import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

const mock = 'http://localhost:8090'

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mock}/mock/reset`)
  await page.addInitScript(() => {
    localStorage.setItem('locale', 'en')
    localStorage.removeItem('mockTier')
  })
})

const bell = (page: Page, name: string | RegExp) => page.getByRole('button', { name })

async function openList(page: Page, name: string | RegExp = 'Notifications, 2 unread') {
  await page.goto('/')
  await bell(page, name).click()
  return page.getByRole('dialog', { name: /Notifications|Сповіщення/ })
}

test('the bell shows how many notifications are unread and lists them newest first', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByTestId('unread-count')).toHaveText('2')

  const dialog = await openList(page)
  const items = dialog.getByRole('listitem')
  await expect(items).toHaveCount(3)
  await expect(items.nth(0)).toContainText('Soil moisture: too low')
  await expect(items.nth(0)).toContainText('Reading: 31.5')
  await expect(items.nth(1)).toContainText('Your controller went offline')
  // The plan change is already read and says what a lower plan means for safety.
  await expect(items.nth(2)).toContainText('Your plan changed from Gardener to Novice')
  await expect(items.nth(2)).toContainText('Safety stays on')
})

test('opening a notification about a pot marks it read and goes to the pot', async ({ page }) => {
  const dialog = await openList(page)
  await dialog.getByRole('button', { name: /Soil moisture: too low/ }).click()

  await expect(page).toHaveURL(/\/pots\//)
  await expect(page.getByRole('heading', { name: 'Kitchen herbs' })).toBeVisible()
  await expect(page.getByTestId('unread-count')).toHaveText('1')
})

test('mark all as read clears the count', async ({ page }) => {
  const dialog = await openList(page)
  await dialog.getByRole('button', { name: 'Mark all as read' }).click()

  await expect(dialog.getByRole('button', { name: 'Mark all as read' })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('unread-count')).toHaveCount(0)
  await expect(bell(page, 'Notifications, nothing unread')).toBeVisible()
})

test('the count follows the server after a notification was read elsewhere', async ({ page, request }) => {
  await page.goto('/')
  await expect(page.getByTestId('unread-count')).toHaveText('2')
  // Another device reads one of them.
  const unread = (await (await request.get(`${mock}/api/greenhaus/v1/notifications?unread=true`)).json()) as { id: string }[]
  await request.post(`${mock}/api/greenhaus/v1/notifications/${unread[0].id}/read`)
  await page.reload()
  await expect(page.getByTestId('unread-count')).toHaveText('1')
})

for (const locale of ['en', 'uk'] as const) {
  test(`the list has no accessibility violations in ${locale}`, async ({ page }) => {
    await page.addInitScript((value) => localStorage.setItem('locale', value), locale)
    await page.goto('/')
    await page.getByRole('button', { name: locale === 'en' ? 'Notifications, 2 unread' : /Сповіщення, 2 непрочитані/ }).click()
    await expect(page.getByRole('dialog')).toBeVisible()
    const results = await new AxeBuilder({ page }).analyze()
    expect(results.violations).toEqual([])
  })
}

test('the list is in Ukrainian when the app is', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
  await page.goto('/')
  await page.getByRole('button', { name: /Сповіщення, 2 непрочитані/ }).click()
  const dialog = page.getByRole('dialog', { name: 'Сповіщення' })
  await expect(dialog).toContainText('Вологість ґрунту: нижче норми')
  await expect(dialog).toContainText('Контролер втратив зв’язок')
  await expect(dialog).toContainText('Безпека лишається ввімкненою')
})