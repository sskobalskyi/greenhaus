import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { limits } from '@/stories/args'
import { TierCard } from './tier-card'

const meta = {
  title: 'Greenhaus/TierCard',
  component: TierCard,
  tags: ['autodocs'],
  decorators: [(Story, { parameters }) => <div className={parameters.wide ? '' : 'max-w-sm'}><Story /></div>],
  args: { tier: 'novice', limits: limits.novice, onChoose: fn() },
} satisfies Meta<typeof TierCard>
export default meta
type Story = StoryObj<typeof meta>

export const Novice: Story = { args: { current: true } }

export const Gardener: Story = {
  args: { tier: 'gardener', limits: limits.gardener, price: { amount: 3, currency: 'USD' } },
  play: async ({ canvasElement, args }) => {
    await userEvent.click(within(canvasElement).getByRole('button', { name: /Choose Gardener/ }))
    await expect(args.onChoose).toHaveBeenCalledWith('gardener')
  },
}

export const Druid: Story = {
  args: { tier: 'druid', limits: limits.druid, price: { amount: 7, currency: 'USD' } },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByText('Unlimited pots')).toBeVisible()
  },
}

export const SideBySide: Story = {
  parameters: { wide: true },
  render: () => (
    <div className="grid gap-4 md:grid-cols-3">
      <TierCard tier="novice" limits={limits.novice} current />
      <TierCard tier="gardener" limits={limits.gardener} price={{ amount: 3, currency: 'USD' }} />
      <TierCard tier="druid" limits={limits.druid} price={{ amount: 7, currency: 'USD' }} />
    </div>
  ),
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: { tier: 'druid', limits: limits.druid, price: { amount: 7, currency: 'USD' }, current: true },
}

/** Before the billing service answers there is no price. A paid tier is never shown as Free. */
export const PriceUnknown: Story = {
  args: { tier: 'gardener', limits: limits.gardener },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByText('Price shown at checkout')).toBeVisible()
    await expect(canvas.queryByText('Free')).toBeNull()
  },
}