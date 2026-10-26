import { Bell, CheckCheck } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { type NotificationItem, useNotificationText } from './notification-text'

export interface NotificationListProps {
  notifications: NotificationItem[]
  /** A notification was opened: mark it read, and go to its pot when it has one. */
  onOpen?: (notification: NotificationItem) => void
  onReadAll?: () => void
  className?: string
}

/** What happened that a person should know, newest first, unread ones marked. */
export function NotificationList({ notifications, onOpen, onReadAll, className }: NotificationListProps) {
  const { t } = useTranslation()
  const text = useNotificationText()
  const unread = notifications.filter((n) => !n.readAt).length

  return (
    <div className={cn('space-y-3', className)}>
      {unread > 0 && (
        <div className="flex justify-end">
          <Button variant="ghost" size="sm" onClick={onReadAll}>
            <CheckCheck className="size-4" aria-hidden />
            {t('notifications.markAll')}
          </Button>
        </div>
      )}
      {notifications.length === 0 ? (
        <p className="flex items-center gap-2 py-6 text-muted-foreground">
          <Bell className="size-5" aria-hidden />
          {t('notifications.empty')}
        </p>
      ) : (
        <ul className="space-y-2">
          {notifications.map((n) => {
            const { title, detail, when } = text(n)
            return (
              <li key={n.id}>
                <button
                  type="button"
                  onClick={() => onOpen?.(n)}
                  className={cn(
                    'flex w-full items-start gap-3 rounded-xl border p-3 text-left transition-colors hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring',
                    n.readAt ? 'bg-card' : 'border-primary/40 bg-secondary/60',
                  )}
                  data-unread={n.readAt ? undefined : 'true'}
                >
                  <span aria-hidden className={cn('mt-1.5 size-2.5 shrink-0 rounded-full', n.readAt ? 'bg-transparent' : 'bg-primary')} />
                  <span className="min-w-0 flex-1">
                    {!n.readAt && <span className="sr-only">{t('notifications.unread')}: </span>}
                    <span className={cn('block', n.readAt ? 'font-medium' : 'font-semibold')}>{title}</span>
                    {detail && <span className="block text-sm text-muted-foreground">{detail}</span>}
                    <span className="block text-xs text-muted-foreground">{when}</span>
                  </span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}