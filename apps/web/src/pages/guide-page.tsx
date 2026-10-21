import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle } from '@greenhaus/ui'
import { ExternalLink, ShieldAlert, ShoppingBasket, Wrench } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { shopUrl } from '~/api/http'

// Example searches, not endorsements. They point at general catalogues so the links do not go stale.
const parts: { key: string; href: string }[] = [
  { key: 'soil', href: 'https://www.adafruit.com/search?q=capacitive+soil+moisture+sensor' },
  { key: 'temperature', href: 'https://www.adafruit.com/search?q=temperature+humidity+sensor' },
  { key: 'light', href: 'https://www.adafruit.com/search?q=light+sensor' },
  { key: 'ph', href: 'https://www.sparkfun.com/search/results?term=ph+probe' },
  { key: 'pump', href: 'https://www.sparkfun.com/search/results?term=peristaltic+pump' },
  { key: 'tank', href: 'https://www.adafruit.com/search?q=water+level+sensor' },
]

export function GuidePage() {
  const { t } = useTranslation()
  const steps = t('guide.qr.steps', { returnObjects: true }) as string[]
  const safety = t('guide.safety.items', { returnObjects: true }) as string[]

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-3xl font-semibold">{t('guide.title')}</h1>
        <p className="text-muted-foreground">{t('guide.intro')}</p>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-xl">
              <ShoppingBasket className="size-5 text-primary" aria-hidden />
              {t('guide.kit.title')}
            </CardTitle>
            <CardDescription>{t('guide.kit.body')}</CardDescription>
          </CardHeader>
          <CardContent>
            <Button asChild>
              <a href={shopUrl}>
                {t('guide.kit.button')}
                <ExternalLink className="size-4" aria-hidden />
              </a>
            </Button>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-xl">
              <Wrench className="size-5 text-primary" aria-hidden />
              {t('guide.diy.title')}
            </CardTitle>
            <CardDescription>{t('guide.diy.body')}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="space-y-2">
              {parts.map((part) => (
                <li key={part.key}>
                  <a href={part.href} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1.5 font-semibold text-primary underline-offset-4 hover:underline">
                    {t(`guide.diy.part.${part.key}`)}
                    <ExternalLink className="size-3.5" aria-hidden />
                  </a>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      </div>

      <section aria-labelledby="qr-title" className="space-y-3">
        <h2 id="qr-title" className="text-2xl font-semibold">
          {t('guide.qr.title')}
        </h2>
        <ol className="space-y-2">
          {steps.map((step, index) => (
            <li key={step} className="flex items-start gap-3 rounded-xl border bg-card p-3">
              <span className="flex size-7 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-bold text-primary-foreground">{index + 1}</span>
              <span>{step}</span>
            </li>
          ))}
        </ol>
        <p className="text-sm text-muted-foreground">{t('guide.qr.note')}</p>
      </section>

      <section aria-labelledby="safety-title" className="space-y-3 rounded-xl border border-attention/50 bg-attention/10 p-5">
        <h2 id="safety-title" className="flex items-center gap-2 text-2xl font-semibold">
          <ShieldAlert className="size-6 text-attention-ink" aria-hidden />
          {t('guide.safety.title')}
        </h2>
        <ul className="list-disc space-y-2 pl-5">
          {safety.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      </section>
    </div>
  )
}
