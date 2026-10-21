import { useTranslation } from 'react-i18next'
import type { Metric, ReadingState } from '@/api/client'
import { defaultUnit, formatValue, metricBand, metricIcon, metricText } from '@/lib/metrics'
import { cn } from '@/lib/utils'

export interface RequirementBandProps {
  metric: Metric
  value?: number | null
  min?: number
  max?: number
  unit?: string
  state?: ReadingState
  /** A tighter layout for cards that show several readings. */
  compact?: boolean
  className?: string
}

/** A reading shown against the target range of the pot. Never an average, always one channel. */
export function RequirementBand({ metric, value, min, max, unit, state = 'ok', compact, className }: RequirementBandProps) {
  const { t, i18n } = useTranslation()
  const Icon = metricIcon[metric]
  const unitText = unit ?? defaultUnit[metric]
  const hasTarget = min !== undefined && max !== undefined
  const width = hasTarget ? max - min : 1
  const low = hasTarget ? min - width * 0.6 : 0
  const high = hasTarget ? max + width * 0.6 : 1
  const place = (n: number) => `${Math.min(97, Math.max(3, ((n - low) / (high - low)) * 100))}%`
  const out = state === 'attention'
  const direction = hasTarget && value != null ? (value < min ? t('band.below') : value > max ? t('band.above') : '') : ''
  const valueText = value == null ? t('band.noValue') : `${formatValue(metric, value, i18n.language)} ${unitText}`.trim()
  const targetText = hasTarget ? t('band.target', { min, max }) : ''

  return (
    <div className={cn('space-y-1.5', className)}>
      <div className="flex items-baseline justify-between gap-2">
        <span className={cn('flex items-center gap-1.5 text-sm font-semibold', metricText[metric])}>
          <Icon className="size-4" aria-hidden />
          <span className="text-foreground">{t(`metric.${metric}`)}</span>
        </span>
        <span className={cn('text-sm tabular-nums', out ? 'font-bold text-foreground' : 'text-muted-foreground')}>
          {valueText}
          {direction && <span className="ml-1 text-xs font-semibold text-attention-ink">{direction}</span>}
        </span>
      </div>
      <div
        // A meter must have a value, so a missing reading is described as an image instead.
        {...(value == null
          ? { role: 'img', 'aria-label': `${t(`metric.${metric}`)}: ${valueText}${targetText ? `, ${targetText}` : ''}` }
          : {
              role: 'meter',
              'aria-label': t(`metric.${metric}`),
              'aria-valuemin': hasTarget ? low : 0,
              'aria-valuemax': hasTarget ? high : 1,
              'aria-valuenow': value,
              'aria-valuetext': `${valueText}${targetText ? `, ${targetText}` : ''}`,
            })}
        className="relative h-3 rounded-full bg-muted"
      >
        {hasTarget && (
          <div
            className={cn('absolute inset-y-0 rounded-full', metricBand[metric])}
            style={{ left: place(min), width: `calc(${place(max)} - ${place(min)})` }}
          />
        )}
        {value != null && hasTarget && (
          <div
            className={cn(
              'absolute top-1/2 size-4 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-background shadow',
              out ? 'bg-attention' : 'bg-ok',
            )}
            style={{ left: place(value) }}
          />
        )}
      </div>
      {!compact && targetText && <p className="text-xs text-muted-foreground">{targetText}</p>}
    </div>
  )
}
