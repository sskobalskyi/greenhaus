import { RotateCcw, Sparkles, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { Metric, Schemas } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Slider } from '@/components/ui/slider'
import { Switch } from '@/components/ui/switch'
import { defaultUnit, formatValue, metricIcon, metrics } from '@/lib/metrics'
import { cn } from '@/lib/utils'

/** The scale of each slider and how fine it moves. These bound what a user may ask for, not what a plant likes. */
const scale: Record<Metric, { min: number; max: number; step: number }> = {
  temperature: { min: 0, max: 45, step: 0.5 },
  air_humidity: { min: 0, max: 100, step: 1 },
  soil_moisture: { min: 0, max: 100, step: 1 },
  ph: { min: 3, max: 9, step: 0.1 },
  light: { min: 0, max: 100_000, step: 500 },
  airflow: { min: 0, max: 3, step: 1 },
}

type Item = Schemas['MetricRequirement']

export interface RequirementsEditorProps {
  requirements: Schemas['Requirements']
  /** What the plants in the pot suggest. Metrics listed in `conflicts` are ones the plants disagree on. */
  suggestion?: Schemas['RequirementsSuggestion']
  saving?: boolean
  onSave?: (requirements: Schemas['Requirements']) => void
  className?: string
}

const itemFor = (requirements: Schemas['Requirements'], metric: Metric): Item =>
  requirements.metrics.find((m) => m.metric === metric) ?? { metric, enabled: false }

/**
 * One set of targets for the whole pot. Each metric can be switched off, such as pH when there is no probe,
 * and the ranges can start from what the plants suggest. Changes stay a draft until saved.
 */
export function RequirementsEditor({ requirements, suggestion, saving, onSave, className }: RequirementsEditorProps) {
  const { t, i18n } = useTranslation()
  const [draft, setDraft] = useState<Schemas['Requirements']>(requirements)
  const [saved, setSaved] = useState(requirements)
  // A fresh set of requirements from the server replaces the draft, which is what saving does too.
  if (saved !== requirements) {
    setSaved(requirements)
    setDraft(requirements)
  }

  const dirty = JSON.stringify(draft) !== JSON.stringify(requirements)
  const conflicts = new Set((suggestion?.conflicts ?? []).map((c) => c.metric))

  const update = (metric: Metric, patch: Partial<Item>) =>
    setDraft({
      metrics: metrics.map((m) => {
        const current = itemFor(draft, m)
        return m === metric ? { ...current, ...patch } : current
      }),
    })

  const enable = (metric: Metric, enabled: boolean) => {
    const current = itemFor(draft, metric)
    const range = scale[metric]
    // Switching a metric on without a range starts from the middle third of its scale.
    const start = current.min ?? range.min + (range.max - range.min) / 3
    const end = current.max ?? range.min + ((range.max - range.min) * 2) / 3
    update(metric, enabled ? { enabled, min: start, max: end } : { enabled })
  }

  return (
    <section className={cn('space-y-4', className)} aria-label={t('editor.title')}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-lg font-semibold">{t('editor.title')}</h3>
        {suggestion && (
          <Button variant="outline" size="sm" className="gap-1.5" onClick={() => setDraft(suggestion.requirements)}>
            <Sparkles className="size-4" aria-hidden />
            {t('editor.useSuggestion')}
          </Button>
        )}
      </div>

      {conflicts.size > 0 && (
        <p role="note" className="flex items-start gap-2 rounded-xl border border-attention/60 bg-attention/10 p-3 text-sm">
          <TriangleAlert className="mt-0.5 size-4 shrink-0 text-attention-ink" aria-hidden />
          {t('editor.conflicts', { metrics: [...conflicts].map((m) => t(`metric.${m}`).toLowerCase()).join(', ') })}
        </p>
      )}

      <ul className="divide-y rounded-xl border bg-card">
        {metrics.map((metric) => {
          const item = itemFor(draft, metric)
          const Icon = metricIcon[metric]
          const range = scale[metric]
          const lo = item.min ?? range.min
          const hi = item.max ?? range.max
          const unit = defaultUnit[metric]
          const name = t(`metric.${metric}`)
          const fmt = (n: number) => formatValue(metric, n, i18n.language)
          return (
            <li key={metric} className="space-y-3 p-4">
              <div className="flex items-center justify-between gap-3">
                <span className="flex items-center gap-2 font-semibold">
                  <Icon className="size-4" aria-hidden />
                  {name}
                  {conflicts.has(metric) && <TriangleAlert className="size-4 text-attention-ink" aria-label={t('editor.conflict')} />}
                </span>
                <Switch checked={item.enabled} onCheckedChange={(on) => enable(metric, on)} aria-label={t('editor.measure', { metric: name })} />
              </div>
              {item.enabled ? (
                <div className="space-y-1">
                  <Slider
                    min={range.min}
                    max={range.max}
                    step={range.step}
                    value={[lo, hi]}
                    minStepsBetweenThumbs={1}
                    onValueChange={([min, max]) => update(metric, { min, max })}
                    thumbLabels={[t('editor.minimum', { metric: name }), t('editor.maximum', { metric: name })]}
                  />
                  <p className="text-sm tabular-nums text-muted-foreground">
                    {fmt(lo)} – {fmt(hi)} {unit}
                  </p>
                </div>
              ) : (
                <p className="text-sm text-muted-foreground">{t('editor.off')}</p>
              )}
            </li>
          )
        })}
      </ul>

      <div className="flex flex-wrap items-center gap-2">
        <Button disabled={!dirty || saving} onClick={() => onSave?.(draft)}>
          {t('editor.save')}
        </Button>
        <Button variant="ghost" disabled={!dirty || saving} onClick={() => setDraft(requirements)} className="gap-1.5">
          <RotateCcw className="size-4" aria-hidden />
          {t('editor.reset')}
        </Button>
      </div>
    </section>
  )
}
