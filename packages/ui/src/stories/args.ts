import type { Schemas } from '@/api/client'

/**
 * Small typed builders for component state in stories. They are not API mocks: live data comes from the
 * mock backend (see the "From mock" stories), so the shapes always follow the contract.
 */
export const zone = (n: number, row: number, col: number): Schemas['Zone'] => ({
  id: `00000000-0000-4000-8000-00000000000${n}`,
  row,
  col,
  label: `Zone ${n}`,
})

export const bedZones: Schemas['Zone'][] = [zone(1, 0, 0), zone(2, 0, 1), zone(3, 1, 0), zone(4, 1, 1)]

export const simplePot: Schemas['Pot'] = {
  id: '10000000-0000-4000-8000-000000000001',
  name: 'Kitchen herbs',
  location: 'Kitchen window',
  kind: 'pot',
  viewMode: 'simple',
  killSwitch: false,
  createdAt: '2026-09-01T08:00:00Z',
}

export const bedPot: Schemas['Pot'] = {
  id: '10000000-0000-4000-8000-000000000002',
  name: 'Balcony bed',
  location: 'Balcony',
  kind: 'bed',
  viewMode: 'zones',
  gridRows: 2,
  gridCols: 2,
  killSwitch: false,
  createdAt: '2026-08-15T08:00:00Z',
}

export const requirements: Schemas['Requirements'] = {
  metrics: [
    { metric: 'temperature', enabled: true, min: 18, max: 27, margin: 0.5 },
    { metric: 'air_humidity', enabled: true, min: 40, max: 60, margin: 1 },
    { metric: 'soil_moisture', enabled: true, min: 55, max: 65, margin: 0.5 },
    { metric: 'light', enabled: true, min: 20000, max: 30000, margin: 500 },
    { metric: 'ph', enabled: true, min: 6, max: 7, margin: 0.1 },
  ],
}

const reading = (
  metric: Schemas['Metric'],
  value: number,
  unit: string,
  state: Schemas['ReadingState'] = 'ok',
  direction?: 'below' | 'above',
): Schemas['MetricReading'] => ({ metric, channelId: `${metric}_z`, value, unit, state, direction })

export const healthyStatus = (zoneId: string): Schemas['PotStatus'] => ({
  potId: simplePot.id,
  state: 'ok',
  zones: [
    {
      zoneId,
      state: 'ok',
      metrics: [
        reading('temperature', 22.4, '°C'),
        reading('air_humidity', 52, '%'),
        reading('soil_moisture', 58, '%'),
        reading('light', 24000, 'lux'),
      ],
    },
  ],
})

export const bedStatus: Schemas['PotStatus'] = {
  potId: bedPot.id,
  state: 'attention',
  worstZoneId: bedZones[2].id,
  zones: [
    { zoneId: bedZones[0].id, state: 'ok', metrics: [reading('soil_moisture', 62, '%'), reading('ph', 6.4, 'pH')] },
    {
      zoneId: bedZones[1].id,
      state: 'attention',
      metrics: [reading('soil_moisture', 63, '%'), reading('ph', 7.6, 'pH', 'attention', 'above')],
    },
    {
      zoneId: bedZones[2].id,
      state: 'attention',
      metrics: [reading('soil_moisture', 41, '%', 'attention', 'below'), reading('ph', 6.3, 'pH')],
    },
    { zoneId: bedZones[3].id, state: 'ok', metrics: [reading('soil_moisture', 60, '%'), reading('ph', 6.5, 'pH')] },
  ],
}

export const pump = (overrides: Partial<Schemas['Actuator']> = {}): Schemas['Actuator'] => ({
  id: 'pump-1',
  kind: 'pump',
  metric: 'soil_moisture',
  mode: 'notify',
  state: 'idle',
  maxModeForTier: 'auto',
  limits: { maxRunSeconds: 30, minIntervalSeconds: 1800, dailyCapSeconds: 240, maxFruitlessRuns: 3 },
  ...overrides,
})

export const limits = {
  novice: {
    pots: 2,
    historyDays: 7,
    aiQuestionsPerMonth: 10,
    aiDataAccess: 'none',
    zoneGridMax: 1,
    automation: [
      { metric: 'soil_moisture', maxMode: 'auto' },
      { metric: 'light', maxMode: 'auto' },
      { metric: 'temperature', maxMode: 'notify' },
    ],
  },
  gardener: {
    pots: 6,
    historyDays: 30,
    aiQuestionsPerMonth: 100,
    aiDataAccess: 'current',
    zoneGridMax: 2,
    automation: [
      { metric: 'soil_moisture', maxMode: 'auto' },
      { metric: 'light', maxMode: 'auto' },
      { metric: 'temperature', maxMode: 'auto' },
      { metric: 'airflow', maxMode: 'auto' },
    ],
  },
  druid: {
    pots: null,
    historyDays: 365,
    aiQuestionsPerMonth: 500,
    aiDataAccess: 'history',
    zoneGridMax: 8,
    automation: [
      { metric: 'soil_moisture', maxMode: 'auto' },
      { metric: 'light', maxMode: 'auto' },
      { metric: 'temperature', maxMode: 'auto' },
      { metric: 'air_humidity', maxMode: 'auto' },
      { metric: 'airflow', maxMode: 'auto' },
    ],
  },
} satisfies Record<Schemas['Tier'], Schemas['Limits']>

export const task = (overrides: Partial<Schemas['CareTask']> = {}): Schemas['CareTask'] => ({
  id: '20000000-0000-4000-8000-000000000001',
  kind: 'fertilize',
  title: 'Fertilize the basil',
  dueAt: '2026-10-24T08:00:00Z',
  state: 'open',
  origin: 'species_schedule',
  ...overrides,
})
