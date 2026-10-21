import { Leaf, OctagonX } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import type { Schemas } from '@/api/client'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'
import { RequirementBand } from './requirement-band'
import { StatusChip } from './status-chip'

export interface PotCardProps {
  pot: Schemas['Pot']
  status: Schemas['PotStatus']
  requirements: Schemas['Requirements']
  zones: Schemas['Zone'][]
  plantCount: number
  className?: string
}

/**
 * A pot at a glance. With several zones it shows the worst zone by name and its readings, never an average.
 */
export function PotCard({ pot, status, requirements, zones, plantCount, className }: PotCardProps) {
  const { t } = useTranslation()
  const shown = status.zones.find((z) => z.zoneId === status.worstZoneId) ?? status.zones[0]
  const shownZone = zones.find((z) => z.id === shown?.zoneId)
  const rangeOf = (metric: Schemas['Metric']) => requirements.metrics.find((m) => m.metric === metric && m.enabled)

  return (
    <Card className={cn('gap-4', status.state === 'attention' && 'border-attention/60', className)}>
      <CardHeader className="gap-1">
        <div className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2">
          <div className="min-w-0">
            <CardTitle className="flex items-center gap-2 text-xl">
              <Leaf className="size-5 shrink-0 text-primary" aria-hidden />
              <span className="truncate">{pot.name}</span>
            </CardTitle>
            <p className="truncate text-sm text-muted-foreground">
              {[t(`pot.kind.${pot.kind}`), pot.location].filter(Boolean).join(' · ')}
            </p>
          </div>
          <StatusChip state={status.state} />
        </div>
        {pot.viewMode === 'zones' && shown && status.state === 'attention' && shownZone && (
          <p className="text-sm font-semibold text-attention-ink">{t('pot.worstZone', { zone: shownZone.label })}</p>
        )}
      </CardHeader>

      <CardContent className="space-y-3">
        {shown && shown.metrics.length > 0 ? (
          shown.metrics.slice(0, 4).map((reading) => {
            const target = rangeOf(reading.metric)
            return (
              <RequirementBand
                key={reading.channelId ?? reading.metric}
                metric={reading.metric}
                value={reading.value}
                unit={reading.unit}
                min={target?.min}
                max={target?.max}
                state={reading.state}
                compact
              />
            )
          })
        ) : (
          <p className="text-sm text-muted-foreground">{t('pot.noReadings')}</p>
        )}
      </CardContent>

      <CardFooter className="flex flex-wrap gap-2">
        <Badge variant="secondary">{t(`pot.view.${pot.viewMode}`)}</Badge>
        {pot.viewMode === 'zones' && <Badge variant="outline">{t('pot.zones', { count: zones.length })}</Badge>}
        <Badge variant="outline">{t('pot.plants', { count: plantCount })}</Badge>
        {pot.killSwitch && (
          <Badge variant="destructive" className="gap-1">
            <OctagonX className="size-3" aria-hidden />
            {t('pot.killSwitch')}
          </Badge>
        )}
      </CardFooter>
    </Card>
  )
}
