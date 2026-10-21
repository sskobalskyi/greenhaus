import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, TierCard } from '@greenhaus/ui'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { useTiers } from '~/api/hooks'
import type { ApiError } from '~/api/http'

/**
 * Shown when the API refuses something because of the plan. The refusal carries the stable code and the tier
 * that unlocks the feature, so the dialog can show that tier's limits in plain words.
 */
export function UpsellDialog({ error, onClose }: { error: ApiError | null; onClose: () => void }) {
  const { t } = useTranslation()
  const tiers = useTiers()
  const required = error?.requiredTier
  const entry = tiers.data?.find((tier) => tier.tier === required)

  return (
    <Dialog open={error !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{t('upsell.title')}</DialogTitle>
          <DialogDescription>{error ? t(`upsell.${error.code === 'limit_reached' ? 'limit_reached' : 'tier_required'}`) : ''}</DialogDescription>
        </DialogHeader>
        {entry && required && <TierCard tier={required} limits={entry.limits} />}
        <DialogFooter className="gap-2 sm:justify-between">
          <Button variant="ghost" onClick={onClose}>
            {t('upsell.later')}
          </Button>
          <Button asChild onClick={onClose}>
            <Link to="/plans">{t('upsell.see')}</Link>
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
