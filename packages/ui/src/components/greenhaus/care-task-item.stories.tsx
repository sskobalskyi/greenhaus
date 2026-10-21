import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { task } from '@/stories/args'
import { CareTaskItem } from './care-task-item'

const meta = {
  title: 'Greenhaus/CareTaskItem',
  component: CareTaskItem,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-md"><Story /></div>],
  args: { task: task(), onComplete: fn() },
} satisfies Meta<typeof CareTaskItem>
export default meta
type Story = StoryObj<typeof meta>

export const Open: Story = {
  play: async ({ canvasElement, args }) => {
    await userEvent.click(within(canvasElement).getByRole('button', { name: 'Mark as done' }))
    await expect(args.onComplete).toHaveBeenCalledWith('20000000-0000-4000-8000-000000000001')
  },
}

export const Done: Story = {
  args: { task: task({ state: 'done', completedAt: '2026-10-20T09:00:00Z' }) },
}

/** A suggestion that came from the pH sensor, not from a schedule. */
export const FromPhSensor: Story = {
  args: { task: task({ id: '20000000-0000-4000-8000-000000000002', kind: 'ph_check', title: 'Zone 2 is more alkaline: check the fertilizer', origin: 'ph_sensor' }) },
}

export const SafetyCheck: Story = {
  args: { task: task({ id: '20000000-0000-4000-8000-000000000003', kind: 'check_pump', title: 'Check the pump and the float switch', origin: 'safety_schedule' }) },
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: { task: task({ kind: 'ph_check', title: 'Зона 2 лужніша: перевірте добриво', origin: 'ph_sensor' }) },
}