import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { pump } from '@/stories/args'
import { ActuatorControl } from './actuator-control'

const meta = {
  title: 'Greenhaus/ActuatorControl',
  component: ActuatorControl,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-md"><Story /></div>],
  args: { actuator: pump(), onModeChange: fn(), onRun: fn() },
} satisfies Meta<typeof ActuatorControl>
export default meta
type Story = StoryObj<typeof meta>

export const SwitchToAuto: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('radio', { name: 'Auto' }))
    await expect(args.onModeChange).toHaveBeenCalledWith('auto')
  },
}

export const ManualRun: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('button', { name: /Run for 10 s/ }))
    await expect(args.onRun).toHaveBeenCalledWith(10)
  },
}

/** A novice cannot automate a heater, so Auto is locked and says which tier unlocks it. */
export const AutoLockedByTier: Story = {
  args: {
    actuator: pump({ id: 'heater-1', kind: 'heater', metric: 'temperature', maxModeForTier: 'notify' }),
    requiredTier: 'gardener',
  },
  play: async ({ canvasElement, args }) => {
    const auto = within(canvasElement).getByRole('radio', { name: 'Auto' })
    await expect(auto).toBeDisabled()
    await userEvent.click(auto, { pointerEventsCheck: 0 })
    await expect(args.onModeChange).not.toHaveBeenCalled()
  },
}

export const KillSwitchOn: Story = {
  args: { killSwitch: true, actuator: pump({ mode: 'auto' }) },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByRole('button', { name: /Run for/ })).toBeDisabled()
  },
}

export const Running: Story = { args: { actuator: pump({ mode: 'auto', state: 'running' }) } }
export const Blocked: Story = { args: { actuator: pump({ mode: 'auto', state: 'blocked' }) } }

export const Lamp: Story = {
  args: {
    actuator: pump({
      id: 'lamp-1',
      kind: 'lamp',
      metric: 'light',
      mode: 'notify',
      limits: { maxRunSeconds: 3600, minIntervalSeconds: 0, dailyCapSeconds: 43200 },
    }),
  },
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: {
    actuator: pump({ id: 'heater-1', kind: 'heater', metric: 'temperature', maxModeForTier: 'notify' }),
    requiredTier: 'gardener',
  },
}