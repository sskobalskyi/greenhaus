import { Check, Sparkles } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import type { Schemas, Tier } from '@/api/client'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'

// Full class names so Tailwind can see them.
const tierLook: Record<Tier, { border: string; badge: string }> = {
  novice: { border: 'border-tier-novice', badge: 'border border-tier-novice bg-tier-novice/20 text-foreground' },
  gardener: { border: 'border-tier-gardener', badge: 'border border-tier-gardener bg-tier-gardener/20 text-foreground' },
  druid: { border: 'border-tier-druid', badge: 'border border-tier-druid bg-tier-druid/20 text-foreground' },
}

export interface TierCardProps {
  tier: Tier
  limits: Schemas['Limits']
  /** Price per month, or undefined for the free tier. */
  price?: { amount: number; currency: string }
  current?: boolean
  onChoose?: (tier: Tier) => void
  className?: string
}

/** One subscription tier with its limits written out in plain words. */
export function TierCard({ tier, limits, price, current, onChoose, className }: TierCardProps) {
  const { t, i18n } = useTranslation()
  const autoMetrics = limits.automation
    .filter((a) => a.maxMode === 'auto')
    .map((a) => t(`metric.${a.metric}`).toLowerCase())
    .join(', ')
  const features = [
    limits.pots == null ? t('tier.potsUnlimited') : t('tier.pots', { count: limits.pots }),
    limits.zoneGridMax > 1 ? t('tier.zonesGrid', { size: limits.zoneGridMax }) : t('tier.zonesSimple'),
    t('tier.history', { days: limits.historyDays }),
    t('tier.aiQuestions', { count: limits.aiQuestionsPerMonth }),
    t(`tier.ai.${limits.aiDataAccess}`),
    t('tier.automation', { metrics: autoMetrics }),
  ]
  const priceText = price
    ? t('tier.perMonth', {
        price: new Intl.NumberFormat(i18n.language, { style: 'currency', currency: price.currency }).format(price.amount),
      })
    : t('tier.free')

  return (
    <Card className={cn('gap-4 border-2', tierLook[tier].border, current && 'shadow-lg', className)}>
      <CardHeader className="gap-2">
        <div className="flex items-center justify-between gap-2">
          <CardTitle className="flex items-center gap-2 text-2xl">
            <Sparkles className="size-5" aria-hidden />
            {t(`tier.name.${tier}`)}
          </CardTitle>
          {current && <Badge className={tierLook[tier].badge}>{t('tier.current')}</Badge>}
        </div>
        <p className="text-sm text-muted-foreground">{t(`tier.tagline.${tier}`)}</p>
        <p className="font-display text-3xl font-semibold">{priceText}</p>
      </CardHeader>
      <CardContent>
        <ul className="space-y-2 text-sm">
          {features.map((feature) => (
            <li key={feature} className="flex items-start gap-2">
              <Check className="mt-0.5 size-4 shrink-0 text-ok" aria-hidden />
              <span>{feature}</span>
            </li>
          ))}
        </ul>
      </CardContent>
      <CardFooter>
        <Button className="w-full" variant={current ? 'secondary' : 'default'} disabled={current} onClick={() => onChoose?.(tier)}>
          {current ? t('tier.current') : t('tier.choose', { tier: t(`tier.name.${tier}`) })}
        </Button>
      </CardFooter>
    </Card>
  )
}
