import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

export interface CalibrationValues {
  dryRaw: number
  wetRaw: number
  applyToAll: boolean
}

export interface CalibrationFormProps {
  /** How many soil moisture sensors the pot has. With more than one the user may reuse the values for all. */
  sensorCount: number
  saving?: boolean
  onSubmit?: (values: CalibrationValues) => void
}

/** Two raw readings, dry and wet, that tell Greenhaus what those mean for this sensor and soil. */
export function CalibrationForm({ sensorCount, saving, onSubmit }: CalibrationFormProps) {
  const { t } = useTranslation()
  const [dry, setDry] = useState('')
  const [wet, setWet] = useState('')
  const [all, setAll] = useState(true)
  const valid = dry !== '' && wet !== '' && Number.isFinite(Number(dry)) && Number.isFinite(Number(wet))
  const same = valid && Number(dry) === Number(wet)

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (valid && !same) onSubmit?.({ dryRaw: Number(dry), wetRaw: Number(wet), applyToAll: all })
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <p className="text-sm text-muted-foreground">{t('calibration.intro')}</p>
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-2">
          <Label htmlFor="calibration-dry">{t('calibration.dry')}</Label>
          <Input id="calibration-dry" inputMode="decimal" value={dry} onChange={(e) => setDry(e.target.value)} placeholder={t('calibration.dryHint')} required />
        </div>
        <div className="space-y-2">
          <Label htmlFor="calibration-wet">{t('calibration.wet')}</Label>
          <Input id="calibration-wet" inputMode="decimal" value={wet} onChange={(e) => setWet(e.target.value)} placeholder={t('calibration.wetHint')} required />
        </div>
      </div>
      {same && (
        <p role="alert" className="text-sm font-semibold text-destructive">
          {t('calibration.differ')}
        </p>
      )}
      {sensorCount > 1 && (
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} className="size-4 accent-primary" />
          {t('calibration.all', { count: sensorCount })}
        </label>
      )}
      <Button type="submit" disabled={!valid || same || saving}>
        {t('calibration.save')}
      </Button>
    </form>
  )
}