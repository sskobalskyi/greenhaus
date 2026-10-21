import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { getJson, type Schemas } from '@/api/client'
import { bedStatus, bedZones, requirements } from '@/stories/args'
import { PotZoneMap } from './pot-zone-map'

const meta = {
  title: 'Greenhaus/PotZoneMap',
  component: PotZoneMap,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-xl"><Story /></div>],
  args: { rows: 2, cols: 2, zones: bedZones, status: bedStatus, requirements, initialMetric: 'soil_moisture' },
} satisfies Meta<typeof PotZoneMap>
export default meta
type Story = StoryObj<typeof meta>

/** Zone 3 is dry. The map shows each zone's own value and never a blended one. */
export const DryZone: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByLabelText(/Zone 3: Soil moisture 41/)).toBeVisible()
    await expect(canvas.getAllByRole('listitem')).toHaveLength(4)
  },
}

export const AlkalineZone: Story = {
  args: { initialMetric: 'ph' },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByLabelText(/Zone 2: pH 7.6/)).toBeVisible()
  },
}

export const SwitchMetric: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('radio', { name: 'pH' }))
    await expect(canvas.getByLabelText(/Zone 2: pH 7.6/)).toBeVisible()
  },
}

export const BigBed: Story = {
  args: {
    rows: 4,
    cols: 4,
    zones: Array.from({ length: 16 }, (_, i) => ({
      id: `00000000-0000-4000-8000-0000000001${String(i).padStart(2, '0')}`,
      row: Math.floor(i / 4),
      col: i % 4,
      label: `Zone ${i + 1}`,
    })),
    status: { potId: bedStatus.potId, state: 'ok', zones: [] },
  },
}

/** Live data from the mock backend: `docker compose up mock` from the repository root. */
export const FromMock: StoryObj<{ rows: number }> = {
  render: (_args, { loaded }) => {
    const data = loaded as { map?: Parameters<typeof PotZoneMap>[0]; error?: string }
    if (data.error) return <p className="text-destructive">{data.error}</p>
    return <PotZoneMap {...data.map!} className="max-w-xl" />
  },
  loaders: [
    async () => {
      try {
        const pots = await getJson<Schemas['Pot'][]>('/pots')
        const bed = pots.find((p) => p.viewMode === 'zones')
        if (!bed) return { error: 'No pot in zones view found in the mock' }
        return {
          map: {
            rows: bed.gridRows ?? 2,
            cols: bed.gridCols ?? 2,
            zones: await getJson<Schemas['Zone'][]>(`/pots/${bed.id}/zones`),
            status: await getJson<Schemas['PotStatus']>(`/pots/${bed.id}/status`),
            requirements: await getJson<Schemas['Requirements']>(`/pots/${bed.id}/requirements`),
          },
        }
      } catch {
        return { error: 'The mock server is not reachable. Start it with: docker compose up mock' }
      }
    },
  ],
  parameters: { a11y: { test: 'todo' } },
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
}