import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { RequirementBand } from './requirement-band'

const meta = {
  title: 'Greenhaus/RequirementBand',
  component: RequirementBand,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-sm"><Story /></div>],
  args: { metric: 'soil_moisture', value: 60, min: 55, max: 65, unit: '%', state: 'ok' },
} satisfies Meta<typeof RequirementBand>
export default meta
type Story = StoryObj<typeof meta>

export const InRange: Story = {
  play: async ({ canvasElement }) => {
    const meter = within(canvasElement).getByRole('meter')
    await expect(meter).toHaveAttribute('aria-valuenow', '60')
    await expect(meter).toHaveAttribute('aria-valuetext', expect.stringContaining('60 %'))
  },
}

export const TooDry: Story = { args: { value: 41, state: 'attention' } }
export const TooWet: Story = { args: { value: 73, state: 'attention' } }
export const NoReading: Story = { args: { value: null, state: 'unknown' } }
export const Compact: Story = { args: { compact: true } }

export const Temperature: Story = {
  args: { metric: 'temperature', value: 31, min: 18, max: 27, unit: '°C', state: 'attention' },
}

export const AllMetrics: Story = {
  render: () => (
    <div className="max-w-sm space-y-4">
      <RequirementBand metric="temperature" value={22} min={18} max={27} unit="°C" />
      <RequirementBand metric="air_humidity" value={52} min={40} max={60} unit="%" />
      <RequirementBand metric="soil_moisture" value={41} min={55} max={65} unit="%" state="attention" />
      <RequirementBand metric="ph" value={6.4} min={6} max={7} unit="pH" />
      <RequirementBand metric="light" value={24000} min={20000} max={30000} unit="lux" />
      <RequirementBand metric="airflow" value={2} min={1} max={2} />
    </div>
  ),
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: { value: 41, state: 'attention' },
}