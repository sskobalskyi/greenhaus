import { describe, expect, it } from 'vitest'
import i18n from '@/i18n'
import { auditText } from './audit-text'

// The sentences are the ones the server writes (Greenhaus.Domain/Automation.cs, TierClamp.cs, ControlEndpoints.cs).
const sentences = {
  requested: '10 s requested by the user',
  mode: 'notify -> auto',
  clamped: 'auto -> notify: the novice plan does not include automatic temperature control',
  clampedEnumName: 'auto -> notify: the novice plan does not include automatic soilmoisture control',
  linked: 'Controller ghx-1234 linked',
  back: 'Soil moisture is back in range, stopped',
  acting: 'Soil moisture is 34, below the target 40: watering for 10 s',
  suggest: 'Soil moisture is 34.1, below the target 40. Run the pump to fix it',
  stale: 'No recent soil moisture reading, so nothing was done',
  cap: 'The daily limit of 120 s for the pump is used up',
  fruitless: '3 runs in a row did not change the soil moisture. The pump is stopped, check it (an empty tank or a broken part)',
}

async function text(locale: 'en' | 'uk', reason: string) {
  await i18n.changeLanguage(locale)
  return auditText(i18n.t, locale, reason)
}

describe('auditText', () => {
  it('says the same in English as the server did', async () => {
    expect(await text('en', sentences.requested)).toBe('You started it for 10 s')
    expect(await text('en', sentences.back)).toBe('Soil moisture is back in range, stopped')
    expect(await text('en', sentences.acting)).toBe('Soil moisture is 34, below the target 40: watering for 10 s')
  })

  it('puts the known sentences into Ukrainian, with numbers in the locale format', async () => {
    expect(await text('uk', sentences.requested)).toBe('Ви запустили на 10 с')
    expect(await text('uk', sentences.mode)).toBe('Режим змінено: Лише сповіщення → Авто')
    expect(await text('uk', sentences.linked)).toBe('Контролер ghx-1234 підключено')
    expect(await text('uk', sentences.back)).toBe('Вологість ґрунту знову в нормі, зупинено')
    expect(await text('uk', sentences.acting)).toBe('Вологість ґрунту: 34, нижче цілі 40. Полив 10 с')
    expect(await text('uk', sentences.suggest)).toContain('34,1')
    expect(await text('uk', sentences.stale)).toContain('Вологість ґрунту')
    expect(await text('uk', sentences.cap)).toContain('120 с')
  })

  it('reads the metric of a tier fallback, which the server writes as the enum name', async () => {
    expect(await text('uk', sentences.clamped)).toContain('«Новак»')
    expect(await text('uk', sentences.clampedEnumName)).toContain('Вологість ґрунту')
  })

  it('uses the plural form of the number of fruitless runs', async () => {
    expect(await text('uk', sentences.fruitless)).toContain('3 запуски поспіль')
    expect(await text('uk', sentences.fruitless.replace('3 runs', '5 runs'))).toContain('5 запусків поспіль')
  })

  it('leaves a sentence it does not know exactly as it came', async () => {
    expect(await text('uk', 'Something new the server says')).toBe('Something new the server says')
  })
})
