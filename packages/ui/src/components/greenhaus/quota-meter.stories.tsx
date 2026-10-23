import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { QuotaMeter } from './quota-meter'

const meta = {
  title: 'Greenhaus/QuotaMeter',
  component: QuotaMeter,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-xs"><Story /></div>],
  args: { used: 3, limit: 10 },
} satisfies Meta<typeof QuotaMeter>
export default meta
type Story = StoryObj<typeof meta>

export const Some: Story = {
  play: async ({ canvasElement }) => {
    const meter = within(canvasElement).getByRole('progressbar', { name: 'Questions used this month' })
    await expect(meter).toHaveAttribute('aria-valuenow', '3')
    await expect(meter).toHaveAttribute('aria-valuemax', '10')
  },
}

export const Empty: Story = { args: { used: 0 } }

/** From nine tenths the bar turns amber. */
export const AlmostFull: Story = { args: { used: 9 } }

export const Full: Story = { args: { used: 10 } }

/** More than the plan allows (a downgrade after asking) is shown as full, never past the end. */
export const OverTheLimit: Story = {
  args: { used: 14 },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByRole('progressbar')).toHaveAttribute('aria-valuenow', '10')
  },
}

export const UkrainianDark: Story = { globals: { locale: 'uk', theme: 'dark' }, args: { used: 9 } }
