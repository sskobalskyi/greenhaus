import { TierCard, type Tier } from '@greenhaus/ui'
import { Info, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useEntitlements, usePrices, useTiers } from '~/api/hooks'
import { CardGridSkeleton, LoadError } from '~/components/feedback'

export function PlansPage() {
  const { t, i18n } = useTranslation()
  const tiers = useTiers()
  const entitlements = useEntitlements()
  const prices = usePrices()
  const [notice, setNotice] = useState(false)
  const date = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'long' })
  const atRisk = entitlements.data?.sources.find((source) => source.status === 'at_risk')

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-semibold">{t('plans.title')}</h1>
        <p className="max-w-3xl text-muted-foreground">{t('plans.subtitle')}</p>
      </div>

      {atRisk && (
        <div role="alert" className="space-y-1 rounded-xl border border-attention/60 bg-attention/10 p-4">
          <p className="flex items-center gap-2 font-semibold">
            <TriangleAlert className="size-5 text-attention-ink" aria-hidden />
            {t('plans.atRiskTitle')}
          </p>
          <p className="text-sm">{t('plans.atRiskBody', { date: atRisk.graceEndsAt ? date.format(new Date(atRisk.graceEndsAt)) : '' })}</p>
        </div>
      )}

      {notice && (
        <p role="status" className="flex items-center gap-2 rounded-xl border bg-secondary p-3 text-sm font-semibold">
          <Info className="size-4" aria-hidden />
          {t('plans.comingSoon')}
        </p>
      )}

      {(tiers.isError || entitlements.isError) && <LoadError onRetry={() => void tiers.refetch()} />}
      {(tiers.isPending || entitlements.isPending) && <CardGridSkeleton />}
      {tiers.data && entitlements.data && (
        <ul className="grid gap-4 md:grid-cols-3">
          {tiers.data.map(({ tier, limits }) => (
            <li key={tier}>
              <TierCard
                tier={tier}
                limits={limits}
                current={entitlements.data.tier === tier}
                price={prices.data?.find((plan) => plan.tier === tier)?.pricePerMonth}
                onChoose={(_chosen: Tier) => setNotice(true)}
                className="h-full"
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
