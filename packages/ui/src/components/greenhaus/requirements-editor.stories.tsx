import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { requirements } from '@/stories/args'
import { RequirementsEditor } from './requirements-editor'

const meta = {
  title: 'Greenhaus/RequirementsEditor',
  component: RequirementsEditor,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-xl"><Story /></div>],
  args: { requirements, onSave: fn() },
} satisfies Meta<typeof RequirementsEditor>
export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    // Nothing to save until something changes.
    await expect(canvas.getByRole('button', { name: 'Save changes' })).toBeDisabled()
    await expect(canvas.getByRole('slider', { name: 'Temperature, minimum' })).toHaveAttribute('aria-valuenow', '18')
  },
}

/** Moving a thumb with the keyboard changes the draft, and only Save sends it. */
export const EditARange: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    const minimum = canvas.getByRole('slider', { name: 'Temperature, minimum' })
    minimum.focus()
    await userEvent.keyboard('{ArrowRight}{ArrowRight}')
    await expect(minimum).toHaveAttribute('aria-valuenow', '19')
    await userEvent.click(canvas.getByRole('button', { name: 'Save changes' }))
    const saved = (args.onSave as ReturnType<typeof fn>).mock.calls[0][0]
    await expect(saved.metrics.find((m: { metric: string }) => m.metric === 'temperature').min).toBe(19)
  },
}

export const DiscardChanges: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const minimum = canvas.getByRole('slider', { name: 'Temperature, minimum' })
    minimum.focus()
    await userEvent.keyboard('{ArrowRight}')
    await userEvent.click(canvas.getByRole('button', { name: 'Discard changes' }))
    await expect(minimum).toHaveAttribute('aria-valuenow', '18')
  },
}

/** pH needs a probe, so it can be switched on or off. Airflow starts off here. */
export const SwitchAMetricOn: Story = {
  args: { requirements: { metrics: requirements.metrics.filter((m) => m.metric !== 'ph').concat({ metric: 'ph', enabled: false }) } },
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('switch', { name: 'Measure pH' }))
    await expect(canvas.getByRole('slider', { name: 'pH, minimum' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Save changes' }))
    const saved = (args.onSave as ReturnType<typeof fn>).mock.calls[0][0]
    await expect(saved.metrics.find((m: { metric: string }) => m.metric === 'ph').enabled).toBe(true)
  },
}

/** Basil and a cactus disagree about soil moisture. The editor says so and offers the suggestion. */
export const WithConflict: Story = {
  args: {
    suggestion: {
      requirements,
      conflicts: [{ metric: 'soil_moisture', plants: ['30000000-0000-4000-8000-000000000001', '30000000-0000-4000-8000-000000000002'] }],
    },
  },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByRole('note')).toHaveTextContent('soil moisture')
    await expect(canvas.getByRole('button', { name: 'Use the suggestion from the plants' })).toBeVisible()
  },
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: { suggestion: { requirements, conflicts: [{ metric: 'soil_moisture', plants: [] }] } },
}