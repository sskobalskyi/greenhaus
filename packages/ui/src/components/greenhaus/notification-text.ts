import { useTranslation } from 'react-i18next'
import type { Schemas } from '@/api/client'

export type NotificationItem = Schemas['Notification']

/**
 * The text of a notification, written from its kind and facts. The server sends no wording, so a change of wording never
 * touches stored data, and a person reads their own language.
 */
export function useNotificationText() {
  const { t, i18n } = useTranslation()
  const time = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' })
  const day = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'long' })
  const tier = (value?: string) => (value ? t(`tier.name.${value}`, { defaultValue: value }) : '')
  const metric = (value?: string) => (value ? t(`metric.${value}`, { defaultValue: value }) : '')

  return (n: NotificationItem): { title: string; detail?: string; when: string } => {
    const p = n.params
    const when = time.format(new Date(n.createdAt))
    switch (n.kind) {
      case 'alert_raised': {
        const where = p.zoneRow && p.zoneCol ? t('notifications.detail.zone', { row: p.zoneRow, col: p.zoneCol }) : undefined
        const value = p.value ? t('notifications.detail.value', { value: p.value }) : undefined
        return {
          title:
            p.rule === 'no_data'
              ? t('notifications.kind.alert_raised_noData', { metric: metric(p.metric) })
              : t('notifications.kind.alert_raised', { metric: metric(p.metric), direction: t(`notifications.direction.${p.direction ?? 'below'}`) }),
          detail: [value, where].filter(Boolean).join(' · ') || undefined,
          when,
        }
      }
      case 'alert_resolved':
        return { title: t('notifications.kind.alert_resolved', { metric: metric(p.metric) }), when }
      case 'actuator_blocked':
        return { title: t('notifications.kind.actuator_blocked', { actuator: t(`actuator.kind.${p.kind ?? p.actuatorId}`, { defaultValue: p.actuatorId ?? '' }) }), detail: p.reason, when }
      case 'care_task_due':
        return { title: t('notifications.kind.care_task_due', { title: p.title ?? '' }), when }
      case 'plan_changed':
        return { title: t('notifications.kind.plan_changed', { from: tier(p.from), to: tier(p.to) }), detail: t('notifications.detail.planChanged', { to: tier(p.to) }), when }
      case 'plan_at_risk':
        return {
          title: t('notifications.kind.plan_at_risk', { tier: tier(p.tier) }),
          detail: p.until ? t('notifications.detail.planAtRisk', { date: day.format(new Date(p.until)) }) : undefined,
          when,
        }
      default:
        return { title: t(`notifications.kind.${n.kind}`, { defaultValue: n.kind }), detail: p.message, when }
    }
  }
}
