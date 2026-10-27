import { Droplets, Flame, Fan, Lightbulb, Lock, Play, ShieldAlert, Waves, type LucideIcon } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { ControlMode, Schemas, Tier } from '@/api/client'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Slider } from '@/components/ui/slider'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'

const kindIcon: Record<Schemas['Actuator']['kind'], LucideIcon> = {
  pump: Droplets,
  heater: Flame,
  lamp: Lightbulb,
  fan: Fan,
  humidifier: Waves,
}

const order: ControlMode[] = ['off', 'notify', 'auto']

export interface ActuatorControlProps {
  actuator: Schemas['Actuator']
  killSwitch?: boolean
  /** The tier that unlocks `auto` for this actuator, shown on the locked option. */
  requiredTier?: Tier
  onModeChange?: (mode: ControlMode) => void
  onRun?: (seconds: number) => void
  className?: string
}

/** Mode, manual run and safety limits of one actuator. Safety limits are shown, never hidden behind a tier. */
export function ActuatorControl({ actuator, killSwitch, requiredTier, onModeChange, onRun, className }: ActuatorControlProps) {
  const { t } = useTranslation()
  const Icon = kindIcon[actuator.kind]
  const max = actuator.maxModeForTier ?? 'auto'
  const maxRun = actuator.limits.maxRunSeconds
  const [seconds, setSeconds] = useState(Math.min(10, maxRun))
  const locked = (mode: ControlMode) => order.indexOf(mode) > order.indexOf(max)

  return (
    <Card className={cn('gap-4', className)}>
      <CardHeader className="flex flex-row items-center justify-between gap-3">
        <CardTitle className="flex items-center gap-2 text-lg">
          <Icon className="size-5 text-primary" aria-hidden />
          {t(`actuator.kind.${actuator.kind}`)}
        </CardTitle>
        <Badge variant={actuator.state === 'fault' || actuator.state === 'blocked' ? 'destructive' : 'secondary'}>
          {t(`actuator.state.${actuator.state}`)}
        </Badge>
      </CardHeader>

      <CardContent className="space-y-4">
        {killSwitch && (
          <p className="flex items-center gap-2 rounded-lg bg-destructive/10 p-2 text-sm font-semibold text-destructive">
            <ShieldAlert className="size-4 shrink-0" aria-hidden />
            {t('actuator.killSwitch')}
          </p>
        )}

        <ToggleGroup
          type="single"
          variant="outline"
          value={actuator.mode}
          onValueChange={(value) => value && onModeChange?.(value as ControlMode)}
          aria-label={t('mode.label')}
          disabled={killSwitch}
          className="w-full items-stretch"
        >
          {order.map((mode) => {
            const isLocked = locked(mode)
            const item = (
              <ToggleGroupItem
                key={mode}
                value={mode}
                disabled={isLocked}
                aria-disabled={isLocked}
                className="h-auto min-h-9 flex-1 gap-1.5 px-2 py-1.5 text-center text-xs whitespace-normal sm:text-sm"
              >
                {isLocked && <Lock className="size-3.5" aria-hidden />}
                {t(`mode.${mode}`)}
              </ToggleGroupItem>
            )
            if (!isLocked) return item
            return (
              <Tooltip key={mode}>
                <TooltipTrigger asChild>
                  <span className="flex flex-1">{item}</span>
                </TooltipTrigger>
                <TooltipContent>
                  {requiredTier ? t('mode.needsTier', { tier: t(`tier.name.${requiredTier}`) }) : t('mode.needsTier', { tier: '+' })}
                </TooltipContent>
              </Tooltip>
            )
          })}
        </ToggleGroup>

        <div className="space-y-2">
          <label htmlFor={`run-${actuator.id}`} className="text-sm font-semibold">
            {t('actuator.duration')}
          </label>
          <div className="flex items-center gap-3">
            <Slider
              id={`run-${actuator.id}`}
              min={1}
              max={maxRun}
              step={1}
              value={[seconds]}
              onValueChange={([value]) => setSeconds(value)}
              disabled={killSwitch}
              aria-label={t('actuator.duration')}
            />
            <Button disabled={killSwitch} onClick={() => onRun?.(seconds)} className="shrink-0 gap-1.5">
              <Play className="size-4" aria-hidden />
              {t('actuator.runFor', { seconds })}
            </Button>
          </div>
        </div>

        <p className="text-xs text-muted-foreground">
          {t('actuator.limits', {
            max: actuator.limits.maxRunSeconds,
            interval: Math.round(actuator.limits.minIntervalSeconds / 60),
            cap: actuator.limits.dailyCapSeconds,
          })}
        </p>
      </CardContent>
    </Card>
  )
}
