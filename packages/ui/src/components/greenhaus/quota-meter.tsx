import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'

export interface QuotaMeterProps {
  /** What has been used so far in the period. */
  used: number
  /** What the plan allows in the period. */
  limit: number
  className?: string
}

/** How much of the monthly assistant questions is used. The bar turns amber from nine tenths and full at the limit. */
export function QuotaMeter({ used, limit, className }: QuotaMeterProps) {
  const { t } = useTranslation()
  const share = limit > 0 ? Math.min(used / limit, 1) : 1
  const label = t('assistant.quota', { used, limit })

  return (
    <div className={cn('space-y-1', className)}>
      <p className="text-sm font-semibold">{label}</p>
      <div
        role="progressbar"
        aria-label={t('assistant.quotaLabel')}
        aria-valuemin={0}
        aria-valuemax={limit}
        aria-valuenow={Math.min(used, limit)}
        aria-valuetext={label}
        className="h-2 w-full overflow-hidden rounded-full bg-secondary"
      >
        <div className={cn('h-full rounded-full transition-[width]', share >= 0.9 ? 'bg-attention' : 'bg-primary')} style={{ width: `${share * 100}%` }} />
      </div>
    </div>
  )
}
