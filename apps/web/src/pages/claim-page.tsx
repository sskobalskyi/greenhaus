import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, metricIcon } from '@greenhaus/ui'
import { CheckCircle2, QrCode, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router'
import { useClaim, useClaimPreview, usePots } from '~/api/hooks'
import { ApiError } from '~/api/http'
import { BlockSkeleton } from '~/components/feedback'

/** The page the QR code opens: d is the device id and t the one-time token. */
export function ClaimPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const deviceId = params.get('d')
  const token = params.get('t')
  const preview = useClaimPreview(deviceId, token)
  const pots = usePots()
  const claim = useClaim()
  const [potId, setPotId] = useState('')

  const reasonOf = (error: unknown) => {
    const code = error instanceof ApiError ? error.code : 'token_invalid'
    const key = `claim.reasons.${code}`
    const text = t(key)
    return text === key ? t('app.errors.unknown') : text
  }

  let body
  if (!deviceId || !token) {
    body = <p>{t('claim.missing')}</p>
  } else if (claim.data) {
    body = (
      <div className="space-y-4">
        <p className="flex items-center gap-2 text-lg font-semibold">
          <CheckCircle2 className="size-6 text-ok" aria-hidden />
          {t('claim.done')}
        </p>
        <Button asChild>
          <Link to={`/pots/${claim.data.potId}?tab=devices`}>{t('claim.open')}</Link>
        </Button>
      </div>
    )
  } else if (preview.isPending) {
    body = <BlockSkeleton />
  } else if (preview.isError) {
    body = (
      <p role="alert" className="flex items-center gap-2 font-semibold text-destructive">
        <TriangleAlert className="size-5" aria-hidden />
        {reasonOf(preview.error)}
      </p>
    )
  } else if (!preview.data.claimable) {
    body = (
      <p role="alert" className="flex items-center gap-2 font-semibold text-destructive">
        <TriangleAlert className="size-5" aria-hidden />
        {t(`claim.reasons.${preview.data.reason ?? 'token_invalid'}`)}
      </p>
    )
  } else {
    body = (
      <div className="space-y-5">
        <div>
          <p className="text-lg font-semibold">{preview.data.model}</p>
          <p className="text-sm text-muted-foreground">{t('claim.device', { id: preview.data.deviceId })}</p>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          {preview.data.channels && preview.data.channels.length > 0 && (
            <div className="space-y-2">
              <p className="text-sm font-semibold">{t('claim.channels')}</p>
              <ul className="flex flex-wrap gap-2">
                {preview.data.channels.map((channel) => {
                  const Icon = metricIcon[channel.metric]
                  return (
                    <li key={channel.channelId}>
                      <Badge variant="secondary" className="gap-1">
                        <Icon className="size-3" aria-hidden />
                        {t(`metric.${channel.metric}`)}
                      </Badge>
                    </li>
                  )
                })}
              </ul>
            </div>
          )}
          {preview.data.actuators && preview.data.actuators.length > 0 && (
            <div className="space-y-2">
              <p className="text-sm font-semibold">{t('claim.actuators')}</p>
              <ul className="flex flex-wrap gap-2">
                {preview.data.actuators.map((actuator) => (
                  <li key={actuator.id}>
                    <Badge variant="outline">{t(`actuator.kind.${actuator.kind}`)}</Badge>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>

        {pots.data && pots.data.length === 0 ? (
          <p className="text-muted-foreground">{t('claim.noPots')}</p>
        ) : (
          <div className="space-y-2">
            <Label htmlFor="claim-pot">{t('claim.pick')}</Label>
            <Select value={potId} onValueChange={setPotId}>
              <SelectTrigger id="claim-pot" className="w-full">
                <SelectValue placeholder={t('claim.choosePot')} />
              </SelectTrigger>
              <SelectContent>
                {pots.data?.map((pot) => (
                  <SelectItem key={pot.id} value={pot.id}>
                    {pot.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}

        {claim.error && (
          <p role="alert" className="font-semibold text-destructive">
            {reasonOf(claim.error)}
          </p>
        )}
        <Button disabled={!potId || claim.isPending} onClick={() => claim.mutate({ deviceId, token, potId })}>
          {t('claim.go')}
        </Button>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-xl space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-2xl">
            <QrCode className="size-6 text-primary" aria-hidden />
            {t('claim.title')}
          </CardTitle>
        </CardHeader>
        <CardContent>{body}</CardContent>
      </Card>
    </div>
  )
}
