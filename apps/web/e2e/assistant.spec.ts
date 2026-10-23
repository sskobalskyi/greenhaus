import AxeBuilder from '@axe-core/playwright'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const mock = 'http://localhost:8090'
const api = `${mock}/api/greenhaus/v1`

test.beforeEach(async ({ page, request }) => {
  await request.post(`${mock}/mock/reset`)
  await page.addInitScript(() => {
    localStorage.setItem('locale', 'en')
    localStorage.removeItem('mockTier')
  })
})

const asTier = (page: Page, tier: string) => page.addInitScript((value) => localStorage.setItem('mockTier', value), tier)

async function openAssistant(page: Page) {
  await page.goto('/')
  await page.getByRole('link', { name: 'Kitchen herbs' }).click()
  await page.getByRole('tab', { name: 'Assistant' }).click()
  await expect(page.getByRole('heading', { name: 'Ask about this pot' })).toBeVisible()
}

/** Uses up questions through the API, so a test that needs the quota nearly gone does not click ten times. */
async function askThroughTheApi(request: APIRequestContext, tier: string, times: number) {
  const pots = (await (await request.get(`${api}/pots`, { headers: { 'X-Mock-Tier': tier } })).json()) as { id: string; name: string }[]
  const pot = pots.find((p) => p.name === 'Kitchen herbs')!
  for (let i = 0; i < times; i++) {
    const response = await request.post(`${api}/pots/${pot.id}/assistant/ask`, { headers: { 'X-Mock-Tier': tier }, data: { question: 'Is it ok?' } })
    expect(response.status()).toBe(200)
    await response.text()
  }
}

const ask = async (page: Page, question: string) => {
  await page.getByLabel('Your question').fill(question)
  await page.getByRole('button', { name: 'Ask', exact: true }).click()
}

const problem = (status: number, body: Record<string, unknown>) => ({
  status,
  contentType: 'application/problem+json',
  body: JSON.stringify({ status, ...body }),
})

test.describe('the assistant of a pot', () => {
  test('answers a question as a stream and counts it against the month', async ({ page }) => {
    await openAssistant(page)
    await expect(page.getByText('0 of 100 questions used this month')).toBeVisible()

    await ask(page, 'Why are the leaves drooping?')
    await expect(page.getByRole('heading', { name: 'Answer' })).toBeVisible()
    await expect(page.getByText(/the plants look fine/)).toBeVisible()
    // The answer is done and the question was counted.
    await expect(page.getByText('1 of 100 questions used this month')).toBeVisible()
  })

  test('says what the plan lets the assistant see', async ({ page }) => {
    await asTier(page, 'novice')
    await openAssistant(page)
    await expect(page.getByText(/not its readings/)).toBeVisible()
  })

  test('a gardener is told the assistant sees the current readings', async ({ page }) => {
    await openAssistant(page)
    await expect(page.getByText(/current readings and open alerts/)).toBeVisible()
  })

  test('a novice asks until the month is used up, then the input is locked', async ({ page, request }) => {
    await askThroughTheApi(request, 'novice', 9)
    await asTier(page, 'novice')
    await openAssistant(page)
    await expect(page.getByText('9 of 10 questions used this month')).toBeVisible()

    await ask(page, 'One more please')
    await expect(page.getByText(/the plants look fine/)).toBeVisible()
    await expect(page.getByText('10 of 10 questions used this month')).toBeVisible()
    await expect(page.getByLabel('Your question')).toBeDisabled()
    await expect(page.getByText(/used all 10 questions/)).toBeVisible()
  })

  test('a refusal for the quota offers the plan that has more', async ({ page }) => {
    // The service says no while the page still shows room, as when the month was used on another device.
    await page.route('**/assistant/ask', (route) =>
      route.fulfill(problem(403, { title: 'Question limit reached', code: 'limit_reached', limitType: 'ai_questions', requiredTier: 'druid' })),
    )
    await openAssistant(page)
    await ask(page, 'Is it ok?')
    const dialog = page.getByRole('dialog', { name: 'This needs a higher plan' })
    await expect(dialog).toBeVisible()
    await expect(dialog).toContainText('Druid')
    await dialog.getByRole('link', { name: 'See plans' }).click()
    await expect(page).toHaveURL(/\/plans$/)
  })

  test('an assistant that is down is explained calmly, and trying again works', async ({ page }) => {
    await page.route('**/assistant/ask', (route) => route.fulfill(problem(503, { title: 'Assistant unavailable', code: 'assistant_unavailable' })), { times: 1 })
    await openAssistant(page)
    await ask(page, 'Is it ok?')
    const alert = page.getByRole('alert')
    await expect(alert).toContainText('not available right now')

    await alert.getByRole('button', { name: 'Try again' }).click()
    await expect(page.getByText(/the plants look fine/)).toBeVisible()
    await expect(page.getByRole('alert')).toHaveCount(0)
  })

  test('shows the passages when the assistant only found documents, and says it did not count', async ({ page }) => {
    const events = [
      { type: 'sources', sources: [{ number: 1, title: 'Basil care', text: 'Basil likes warm, bright places.' }] },
      { type: 'done', mode: 'retrieval_only', reason: 'budget_exceeded' },
    ]
    await page.route('**/assistant/ask', (route) =>
      route.fulfill({ status: 200, contentType: 'text/event-stream', body: events.map((e) => `data: ${JSON.stringify(e)}\n\n`).join('') }),
    )
    await openAssistant(page)
    await ask(page, 'How do I water basil?')
    await expect(page.getByText(/did not count as one of your questions/)).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Sources' })).toBeVisible()
    await expect(page.getByText('Basil care')).toBeVisible()
  })

  test('is in Ukrainian when the app is', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).click()
    await page.getByRole('tab', { name: 'Помічник' }).click()
    await expect(page.getByRole('heading', { name: 'Запитайте про цей горщик' })).toBeVisible()
    await expect(page.getByText(/Використано запитань цього місяця: 0 із 100/)).toBeVisible()

    await page.getByLabel('Ваше запитання').fill('Чому в’януть листки?')
    await page.getByRole('button', { name: 'Запитати', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Відповідь' })).toBeVisible()
    await expect(page.getByText(/Використано запитань цього місяця: 1 із 100/)).toBeVisible()
  })
})

test.describe('the assistant tab has no accessibility violations', () => {
  const scan = async (page: Page) => {
    await page.waitForLoadState('networkidle')
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([])
  }

  test('before asking, in English', async ({ page }) => {
    await openAssistant(page)
    await scan(page)
  })

  test('with an answer, in English', async ({ page }) => {
    await openAssistant(page)
    await ask(page, 'Why are the leaves drooping?')
    await expect(page.getByText(/the plants look fine/)).toBeVisible()
    await scan(page)
  })

  test('with an answer, in Ukrainian and dark', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('locale', 'uk'))
    await page.goto('/')
    await page.getByRole('link', { name: 'Kitchen herbs' }).click()
    await page.getByRole('tab', { name: 'Помічник' }).click()
    await page.getByLabel('Ваше запитання').fill('Чому в’януть листки?')
    await page.getByRole('button', { name: 'Запитати', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Відповідь' })).toBeVisible()
    await page.evaluate(() => document.documentElement.classList.add('dark'))
    await scan(page)
  })

  test('when the month is used up', async ({ page, request }) => {
    await askThroughTheApi(request, 'novice', 10)
    await asTier(page, 'novice')
    await openAssistant(page)
    await expect(page.getByText(/used all 10 questions/)).toBeVisible()
    await scan(page)
  })
})
