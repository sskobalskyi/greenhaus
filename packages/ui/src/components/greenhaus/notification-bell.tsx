import { Bell } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'
import { NotificationList } from './notification-list'
import type { NotificationItem } from './notification-text'

export interface NotificationBellProps {
  notifications: NotificationItem[]
  /** A notification was opened: mark it read, and go to its pot when it has one. */
  onOpen?: (notification: NotificationItem) => void
  onReadAll?: () => void
}

/** The bell in the header: the unread count on it, the list in a dialog. */
export function NotificationBell({ notifications, onOpen, onReadAll }: NotificationBellProps) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const unread = notifications.filter((n) => !n.readAt).length

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="icon" className="relative" aria-label={unread > 0 ? t('notifications.bell', { count: unread }) : t('notifications.bellNone')}>
          <Bell className="size-5" aria-hidden />
          {unread > 0 && (
            <span
              aria-hidden
              className="absolute -right-0.5 -top-0.5 flex min-w-5 items-center justify-center rounded-full bg-primary px-1 text-xs font-bold leading-5 text-primary-foreground"
              data-testid="unread-count"
            >
              {unread > 99 ? '99+' : unread}
            </span>
          )}
        </Button>
      </DialogTrigger>
      <DialogContent className="max-h-[85svh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t('notifications.title')}</DialogTitle>
          <DialogDescription className="sr-only">{t('notifications.title')}</DialogDescription>
        </DialogHeader>
        <NotificationList
          notifications={notifications}
          onReadAll={onReadAll}
          onOpen={(n) => {
            onOpen?.(n)
            if (n.potId) setOpen(false)
          }}
        />
      </DialogContent>
    </Dialog>
  )
}