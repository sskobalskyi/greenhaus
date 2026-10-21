import { ArrowDown, ArrowUp } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { Metric, ReadingState, Schemas } from '@/api/client'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { defaultUnit, formatValue, metricIcon, metrics } from '@/lib/metrics'
import { zoneName } from '@/lib/zone'
import { cn } from '@/lib/utils'

export interface PotZoneMapProps {
  rows: number
  cols: number
  zones: Schemas['Zone'][]
  status: Schemas['PotStatus']
  requirements: Schemas['Requirements']
  /** The metric shown first. Defaults to the one that needs attention in the worst zone. */
  initialMetric?: Metric
  className?: string
}

const cellLook: Record<ReadingState, string> = {
  ok: 'border-ok/40 bg-ok/15',
  attention: 'border-attention bg-attention/25',
  offline: 'border-offline/40 bg-offline/10',
  unknown: 'border-dashed border-border bg-muted/60',
}

/** A heatmap of one metric across the zones of a pot or bed. The pot is never summarised as a single number. */
export function PotZoneMap({ rows, cols, zones, status, requirements, initialMetric, className }: PotZoneMapProps) {
  const { t, i18n } = useTranslation()
  const available = metrics.filter((m) => status.zones.some((z) => z.metrics.some((r) => r.metric === m)))
  // Open on the metric that needs attention in the worst zone, so the problem is the first thing you see.
  const problem = status.zones.find((z) => z.zoneId === status.worstZoneId)?.metrics.find((r) => r.state === 'attention')?.metric
  const [metric, setMetric] = useState<Metric>(initialMetric ?? problem ?? available[0] ?? 'soil_moisture')
  const worst = zones.find((z) => z.id === status.worstZoneId)
  const nameOf = (zone: Schemas['Zone']) => zoneName(t, zone, { simple: false, cols })

  return (
    <section className={cn('space-y-3', className)} aria-label={t('zoneMap.title')}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-lg font-semibold">{t('zoneMap.title')}</h3>
        <ToggleGroup
          type="single"
          value={metric}
          onValueChange={(value) => value && setMetric(value as Metric)}
          variant="outline"
          size="sm"
          aria-label={t('zoneMap.metric')}
        >
          {available.map((m) => {
            const Icon = metricIcon[m]
            return (
              <ToggleGroupItem key={m} value={m} aria-label={t(`metric.${m}`)}>
                <Icon className="size-4" aria-hidden />
                <span className="hidden sm:inline">{t(`metric.${m}`)}</span>
              </ToggleGroupItem>
            )
          })}
        </ToggleGroup>
      </div>

      <ul className="grid gap-2" style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))` }}>
        {Array.from({ length: rows * cols }, (_, index) => {
          const zone = zones.find((z) => z.row === Math.floor(index / cols) && z.col === index % cols)
          if (!zone) return <li key={index} aria-hidden />
          const reading = status.zones.find((z) => z.zoneId === zone.id)?.metrics.find((r) => r.metric === metric)
          const state: ReadingState = reading?.state ?? 'unknown'
          const target = requirements.metrics.find((m) => m.metric === metric && m.enabled)
          const unit = reading?.unit ?? defaultUnit[metric]
          const valueText = reading?.value == null ? t('zoneMap.noData') : `${formatValue(metric, reading.value, i18n.language)} ${unit}`.trim()
          return (
            <li
              key={zone.id}
              aria-label={t('zoneMap.cell', { zone: nameOf(zone), metric: t(`metric.${metric}`), value: valueText, state: t(`status.${state}`) })}
              className={cn(
                'flex min-h-20 flex-col justify-between rounded-xl border-2 p-2.5 text-sm',
                cellLook[state],
                zone.id === status.worstZoneId && 'ring-2 ring-attention ring-offset-2 ring-offset-background',
              )}
            >
              <span className="font-semibold">{nameOf(zone)}</span>
              <span className="flex items-center gap-1 text-base font-bold tabular-nums">
                {valueText}
                {reading?.direction === 'above' && <ArrowUp className="size-4 text-attention-ink" aria-hidden />}
                {reading?.direction === 'below' && <ArrowDown className="size-4 text-attention-ink" aria-hidden />}
              </span>
              {target?.min !== undefined && target.max !== undefined && (
                <span className="text-xs text-foreground">{t('band.target', { min: formatValue(metric, target.min, i18n.language), max: formatValue(metric, target.max, i18n.language) })}</span>
              )}
            </li>
          )
        })}
      </ul>

      {worst && <p className="text-sm font-semibold text-attention-ink">{t('zoneMap.worst', { zone: nameOf(worst) })}</p>}
    </section>
  )
}
