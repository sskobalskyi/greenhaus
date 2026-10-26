import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { notification, notificationSet } from '@/stories/args'
import { NotificationBell } from './notification-bell'

const meta = {
  title: 'Greenhaus/NotificationBell',
  component: NotificationBell,
  tags: ['autodocs'],
  args: { notifications: notificationSet(), onOpen: fn(), onReadAll: fn() },
} satisfies Meta<typeof NotificationBell>
export default meta
type Story = StoryObj<typeof meta>

/** The unread count sits on the bell, and the list opens in a dialog. */
export const Unread: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByTestId('unread-count')).toHaveTextContent('8')
    await userEvent.click(canvas.getByRole('button', { name: 'Notifications, 8 unread' }))
    const dialog = within(document.body).getByRole('dialog', { name: 'Notifications' })
    await expect(within(dialog).getByText('Soil moisture: too low')).toBeInTheDocument()
  },
}

export const NothingUnread: Story = {
  args: { notifications: [notification({ readAt: '2026-10-22T09:35:00Z' })] },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.queryByTestId('unread-count')).toBeNull()
    await expect(canvas.getByRole('button', { name: 'Notifications, nothing unread' })).toBeInTheDocument()
  },
}

export const UkrainianDark: Story = { globals: { locale: 'uk', theme: 'dark' } }