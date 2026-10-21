import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { getJson, type Schemas } from '@/api/client'
import { bedPot, bedStatus, bedZones, healthyStatus, requirements, simplePot, zone } from '@/stories/args'
import { PotCard } from './pot-card'

const kitchenZone = zone(0, 0, 0)

const meta = {
  title: 'Greenhaus/PotCard',
  component: PotCard,
  tags: ['autodocs'],
  decorators: [(Story, { parameters }) => <div className={parameters.wide ? '' : 'max-w-sm'}><Story /></div>],
  args: {
    pot: simplePot,
    status: healthyStatus(kitchenZone.id),
    requirements,
    zones: [kitchenZone],
    plantCount: 2,
  },
} satisfies Meta<typeof PotCard>
export default meta
type Story = StoryObj<typeof meta>

export const Healthy: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByText('Kitchen herbs')).toBeVisible()
    await expect(canvas.getAllByRole('meter')).toHaveLength(4)
  },
}

/** A bed with four zones: the card names the worst zone and shows its readings, not an average. */
export const NeedsAttentionInOneZone: Story = {
  args: { pot: bedPot, status: bedStatus, zones: bedZones, plantCount: 3 },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByText(/Zone 3/)).toBeVisible()
  },
}

export const KillSwitchOn: Story = { args: { pot: { ...simplePot, killSwitch: true } } }

export const WaitingForReadings: Story = {
  args: { status: { potId: simplePot.id, state: 'unknown', zones: [{ zoneId: kitchenZone.id, state: 'unknown', metrics: [] }] } },
}

/** Live data from the mock backend: `docker compose up mock` from the repository root. */
export const FromMock: StoryObj<{ pot: Schemas['Pot'] }> = {
  render: (_args, { loaded }) => {
    const data = loaded as { cards?: Array<{ pot: Schemas['Pot']; status: Schemas['PotStatus']; requirements: Schemas['Requirements']; zones: Schemas['Zone'][]; plantCount: number }>; error?: string }
    if (data.error) return <p className="text-destructive">{data.error}</p>
    return (
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {data.cards?.map((card) => <PotCard key={card.pot.id} {...card} />)}
      </div>
    )
  },
  loaders: [
    async () => {
      try {
        const pots = await getJson<Schemas['Pot'][]>('/pots')
        const cards = await Promise.all(
          pots.map(async (pot) => ({
            pot,
            status: await getJson<Schemas['PotStatus']>(`/pots/${pot.id}/status`),
            requirements: await getJson<Schemas['Requirements']>(`/pots/${pot.id}/requirements`),
            zones: await getJson<Schemas['Zone'][]>(`/pots/${pot.id}/zones`),
            plantCount: (await getJson<Schemas['Plant'][]>(`/pots/${pot.id}/plants`)).length,
          })),
        )
        return { cards }
      } catch {
        return { error: 'The mock server is not reachable. Start it with: docker compose up mock' }
      }
    },
  ],
  parameters: { wide: true, a11y: { test: 'todo' } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    // Without the mock the story shows a hint instead, which is fine for CI.
    if (canvas.queryByText(/not reachable/)) return
    await expect(await canvas.findByText('Kitchen herbs')).toBeVisible()
    await expect(await canvas.findByText('Balcony bed')).toBeVisible()
  },
}

/** Dark theme and Ukrainian, so contrast and long words are checked too. */
export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: { pot: bedPot, status: bedStatus, zones: bedZones, plantCount: 5 },
}