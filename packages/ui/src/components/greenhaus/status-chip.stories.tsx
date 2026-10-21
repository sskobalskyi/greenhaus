import type { Meta, StoryObj } from '@storybook/react-vite'
import { StatusChip } from './status-chip'

const meta = { title: 'Greenhaus/StatusChip', component: StatusChip, tags: ['autodocs'] } satisfies Meta<typeof StatusChip>
export default meta
type Story = StoryObj<typeof meta>

export const Ok: Story = { args: { state: 'ok' } }
export const Attention: Story = { args: { state: 'attention' } }
export const Offline: Story = { args: { state: 'offline' } }
export const Unknown: Story = { args: { state: 'unknown' } }

export const AllStates: Story = {
  args: { state: 'ok' },
  render: () => (
    <div className="flex flex-wrap gap-2">
      {(['ok', 'attention', 'offline', 'unknown'] as const).map((state) => (
        <StatusChip key={state} state={state} />
      ))}
    </div>
  ),
}
