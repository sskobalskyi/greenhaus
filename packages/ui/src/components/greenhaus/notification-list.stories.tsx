import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { notification, notificationSet } from '@/stories/args'
import { NotificationList } from './notification-list'

const meta = {
  title: 'Greenhaus/NotificationList',
  component: NotificationList,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-lg"><Story /></div>],
  args: { notifications: notificationSet(), onOpen: fn(), onReadAll: fn() },
} satisfies Meta<typeof NotificationList>
export default meta
type Story = StoryObj<typeof meta>

/** Every kind of notification, unread ones marked, the plan notes included. */
export const EveryKind: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByText('Soil moisture: too low')).toBeInTheDocument()
    await expect(canvas.getByText('Temperature: too high')).toBeInTheDocument()
    await expect(canvas.getByText(/Row 1, column 2/)).toBeInTheDocument()
    await expect(canvas.getByText('No readings of Soil moisture')).toBeInTheDocument()
    await expect(canvas.getByText('Your plan changed from Druid to Gardener')).toBeInTheDocument()
    await userEvent.click(canvas.getByRole('button', { name: /Your controller went offline/ }))
    await expect(args.onOpen).toHaveBeenCalled()
    await userEvent.click(canvas.getByRole('button', { name: 'Mark all as read' }))
    await expect(args.onReadAll).toHaveBeenCalled()
  },
}

export const Empty: Story = { args: { notifications: [] } }

/** With nothing unread there is no "mark all" button. */
export const AllRead: Story = {
  args: { notifications: [notification({ readAt: '2026-10-22T09:35:00Z' }), notification({ kind: 'care_task_due', params: { title: 'Turn the pot' }, readAt: '2026-10-22T09:35:00Z' })] },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).queryByRole('button', { name: 'Mark all as read' })).toBeNull()
  },
}

export const Dark: Story = { globals: { theme: 'dark' } }

export const Ukrainian: Story = { globals: { locale: 'uk' } }

export const UkrainianDark: Story = { globals: { locale: 'uk', theme: 'dark' } }