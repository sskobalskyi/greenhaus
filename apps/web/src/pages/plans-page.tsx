import { Button, TierCard, type Tier } from '@greenhaus/ui'
import { Info, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { signIn, useAuth } from '~/auth/auth'
import { useCancelSubscription, useEntitlements, usePrices, useSubscribe, useSubscription, useTiers } from '~/api/hooks'
import { ApiError } from '~/api/http'
import { CardGridSkeleton, LoadError } from '~/components/feedback'

export function PlansPage() {
  const { t, i18n } = useTranslation()
  const auth = useAuth()
  const signedOut = auth.enabled && !auth.loading && !auth.user
  const tiers = useTiers()
  const entitlements = useEntitlements()
  const prices = usePrices()
  const subscription = useSubscription()
  const subscribe = useSubscribe()
  const cancel = useCancelSubscription()
  const [notice, setNotice] = useState<string | null>(null)
  const [declineCard, setDeclineCard] = useState(false)
  const date = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'long' })
  const atRisk = entitlements.data?.sources.find((source) => source.status === 'at_risk')
  const paid = subscription.data && subscription.data.status !== 'cancelled' ? subscription.data : null
  const billingDown = prices.isError

  const choose = (tier: Tier) => {
    setNotice(null)
    // Choosing a plan needs an account: sign in or register, then come back here.
    if (signedOut) {
      void signIn('/plans')
      return
    }
    // The free tier is not bought: choosing it ends the paid plan at the end of the period.
    if (tier === 'novice') {
      cancel.mutate()
      return
    }
    subscribe.mutate(
      // The demo billing service declines a card whose token ends in "-declined".
      { tier, paymentMethodToken: declineCard ? 'tok-declined' : 'tok-demo' },
      {
        onSuccess: () => setNotice(t('plans.subscribed', { tier: t(`tier.name.${tier}`) })),
        onError: (error) => setNotice(error instanceof ApiError && error.status === 402 ? t('plans.declined') : t('app.errors.unknown')),
      },
    )
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-semibold">{t('plans.title')}</h1>
        <p className="max-w-3xl text-muted-foreground">{t('plans.subtitle')}</p>
      </div>

      {signedOut && (
        <p role="note" className="flex items-center gap-2 rounded-xl border bg-secondary p-3 text-sm font-semibold">
          <Info className="size-4" aria-hidden />
          {t('plans.signInPrompt')}
        </p>
      )}

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
          {notice}
        </p>
      )}

      {billingDown && (
        <p role="note" className="text-sm text-muted-foreground">
          {t('plans.billingDown')}
        </p>
      )}

      {paid && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
          <p className="text-sm">
            {paid.cancelAtPeriodEnd
              ? t('plans.endsOn', { tier: t(`tier.name.${paid.tier}`), date: date.format(new Date(paid.currentPeriodEnd)) })
              : t('plans.activeUntil', { tier: t(`tier.name.${paid.tier}`), date: date.format(new Date(paid.currentPeriodEnd)) })}
          </p>
          {!paid.cancelAtPeriodEnd && (
            <Button variant="outline" size="sm" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              {t('plans.cancel')}
            </Button>
          )}
        </div>
      )}

      {import.meta.env.VITE_MOCK !== 'false' && !billingDown && (
        <label className="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" checked={declineCard} onChange={(event) => setDeclineCard(event.target.checked)} className="size-4 accent-primary" />
          {t('plans.simulateDecline')}
        </label>
      )}

      {(tiers.isError || entitlements.isError) && <LoadError onRetry={() => void tiers.refetch()} />}
      {tiers.isPending && <CardGridSkeleton />}
      {tiers.data && (entitlements.data || signedOut) && (
        <ul className="grid gap-4 md:grid-cols-3">
          {tiers.data.map(({ tier, limits }) => (
            <li key={tier}>
              <TierCard
                tier={tier}
                limits={limits}
                current={entitlements.data?.tier === tier}
                price={prices.data?.find((plan) => plan.tier === tier)?.pricePerMonth}
                onChoose={choose}
                className="h-full"
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
