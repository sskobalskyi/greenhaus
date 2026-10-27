import {
  ActuatorControl,
  Badge,
  Button,
  CareTaskItem,
  Input,
  Label,
  PotZoneMap,
  RequirementBand,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  StatusChip,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  ToggleGroup,
  ToggleGroupItem,
  auditText,
  zoneName,
  type ControlMode,
  type Schemas,
} from '@greenhaus/ui'
import { ArrowLeft, OctagonX, Trash2, TriangleAlert } from 'lucide-react'
import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams, useSearchParams } from 'react-router'
import {
  useActuators,
  useAddPlant,
  useAuditLog,
  useCareTasks,
  useCompleteTask,
  useDevices,
  useKillSwitch,
  usePlants,
  usePot,
  usePotStatus,
  useRemovePlant,
  useRequirements,
  useRunActuator,
  useSetMode,
  useSetViewMode,
  useSpecies,
  useSuggestion,
  useTiers,
  useZones,
} from '~/api/hooks'
import { ApiError } from '~/api/http'
import { AssistantTab } from '~/components/assistant-tab'
import { BlockSkeleton, LoadError } from '~/components/feedback'
import { CalibrationDialog, CareForm, PotActions, RequirementsTab } from '~/components/pot-tools'
import { UpsellDialog } from '~/components/upsell-dialog'

type Pot = Schemas['Pot']

const tabs = ['overview', 'plants', 'requirements', 'controls', 'care', 'devices', 'assistant'] as const

function errorText(t: (key: string) => string, error: unknown): string {
  if (error instanceof ApiError) {
    const key = `app.errors.${error.code}`
    const text = t(key)
    return text === key ? t('app.errors.unknown') : text
  }
  return t('app.errors.unknown')
}

// ---- overview -------------------------------------------------------------------------------------

function ViewModeSwitch({ pot, status, onPlanLimit }: { pot: Pot; status: Schemas['PotStatus']; onPlanLimit: (error: ApiError) => void }) {
  const { t } = useTranslation()
  const setView = useSetViewMode(pot.id)

  const change = (mode: string) => {
    if (!mode || mode === pot.viewMode) return
    // Back to Simple keeps one channel per metric, the first one that reports.
    const primary = new Map<string, string>()
    for (const zone of status.zones) for (const reading of zone.metrics) if (reading.channelId && !primary.has(reading.metric)) primary.set(reading.metric, reading.channelId)
    const body: Schemas['ViewModeUpdate'] =
      mode === 'zones'
        ? { mode: 'zones', gridRows: 2, gridCols: 2 }
        : { mode: 'simple', primaryChannels: [...primary].map(([metric, channelId]) => ({ metric: metric as Schemas['Metric'], channelId })) }
    setView.mutate(body, { onError: (error) => error instanceof ApiError && error.isPlanLimit && onPlanLimit(error) })
  }

  return (
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-sm font-semibold">{t('pot.view.label')}</span>
      <ToggleGroup type="single" variant="outline" size="sm" value={pot.viewMode} onValueChange={change} aria-label={t('pot.view.label')}>
        <ToggleGroupItem value="simple">{t('pot.view.simple')}</ToggleGroupItem>
        <ToggleGroupItem value="zones">{t('pot.view.zones')}</ToggleGroupItem>
      </ToggleGroup>
      {setView.error && !(setView.error instanceof ApiError && setView.error.isPlanLimit) && (
        <span role="alert" className="text-sm text-destructive">
          {t('pot.view.changeFailed')}
        </span>
      )}
    </div>
  )
}

function Overview({ pot, onPlanLimit }: { pot: Pot; onPlanLimit: (error: ApiError) => void }) {
  const { t } = useTranslation()
  const status = usePotStatus(pot.id)
  const zones = useZones(pot.id)
  const requirements = useRequirements(pot.id)

  if (status.isError || zones.isError || requirements.isError) return <LoadError />
  if (!status.data || !zones.data || !requirements.data) return <BlockSkeleton className="h-72" />
  const target = (metric: Schemas['Metric']) => requirements.data.metrics.find((m) => m.metric === metric && m.enabled)
  const grid = { simple: pot.viewMode === 'simple', cols: pot.gridCols ?? 1 }

  return (
    <div className="space-y-6">
      <ViewModeSwitch pot={pot} status={status.data} onPlanLimit={onPlanLimit} />

      {pot.viewMode === 'zones' ? (
        <PotZoneMap rows={pot.gridRows ?? 2} cols={pot.gridCols ?? 2} zones={zones.data} status={status.data} requirements={requirements.data} />
      ) : (
        <div className="grid gap-5 sm:grid-cols-2">
          {status.data.zones[0]?.metrics.map((reading) => (
            <RequirementBand
              key={reading.channelId ?? reading.metric}
              metric={reading.metric}
              value={reading.value}
              unit={reading.unit}
              min={target(reading.metric)?.min}
              max={target(reading.metric)?.max}
              state={reading.state}
            />
          ))}
        </div>
      )}

      <section aria-labelledby="alerts-title" className="space-y-2">
        <h2 id="alerts-title" className="text-xl font-semibold">
          {t('pot.alerts')}
        </h2>
        {status.data.alerts && status.data.alerts.length > 0 ? (
          <ul className="space-y-2">
            {status.data.alerts.map((alert) => {
              const zone = zones.data.find((z) => z.id === alert.zoneId)
              const reading = status.data.zones.find((z) => z.zoneId === alert.zoneId)?.metrics.find((m) => m.metric === alert.metric)
              return (
                <li key={alert.id} className="flex items-center gap-2 rounded-xl border border-attention/50 bg-attention/10 p-3 text-sm">
                  <TriangleAlert className="size-4 shrink-0 text-attention-ink" aria-hidden />
                  <span>
                    {zone && grid.simple === false ? `${zoneName(t, zone, grid)} · ` : ''}
                    <strong>{t(`metric.${alert.metric}`)}</strong> {reading?.direction ? t(`band.${reading.direction}`) : ''}
                  </span>
                </li>
              )
            })}
          </ul>
        ) : (
          <p className="text-muted-foreground">{t('pot.noAlerts')}</p>
        )}
      </section>
    </div>
  )
}

// ---- plants ---------------------------------------------------------------------------------------

function Plants({ pot }: { pot: Pot }) {
  const { t, i18n } = useTranslation()
  const plants = usePlants(pot.id)
  const species = useSpecies()
  const suggestion = useSuggestion(pot.id)
  const add = useAddPlant(pot.id)
  const remove = useRemovePlant(pot.id)
  const [speciesId, setSpeciesId] = useState('')
  const [nickname, setNickname] = useState('')

  if (plants.isError || species.isError) return <LoadError />
  if (!plants.data || !species.data) return <BlockSkeleton />
  // The catalog has English common names only, so other languages bring their own and the catalog name is the fallback.
  const label = (id: string, fallback: string) => t(`species.${id}`, { defaultValue: fallback })
  const nameOf = (id: string) => label(id, species.data.find((s) => s.id === id)?.commonName ?? id)
  const conflicts = suggestion.data?.conflicts ?? []

  const submit = (event: FormEvent) => {
    event.preventDefault()
    add.mutate({ speciesId, nickname: nickname.trim() }, { onSuccess: () => setNickname('') })
  }

  return (
    <div className="space-y-6">
      {conflicts.length > 0 && (
        <div role="note" className="space-y-1 rounded-xl border border-attention/60 bg-attention/10 p-4">
          <p className="flex items-center gap-2 font-semibold">
            <TriangleAlert className="size-5 text-attention-ink" aria-hidden />
            {t('pot.plants.conflictTitle')}
          </p>
          <p className="text-sm">{t('pot.plants.conflictBody', { metrics: conflicts.map((c) => t(`metric.${c.metric}`).toLowerCase()).join(', ') })}</p>
        </div>
      )}

      <section aria-labelledby="plants-title" className="space-y-3">
        <h2 id="plants-title" className="text-xl font-semibold">
          {t('pot.plants.title')}
        </h2>
        {plants.data.length === 0 && <p className="text-muted-foreground">{t('pot.plants.empty')}</p>}
        <ul className="space-y-2">
          {plants.data.map((plant) => (
            <li key={plant.id} className="flex items-center justify-between gap-3 rounded-xl border bg-card p-3">
              <div>
                <p className="font-semibold">{plant.nickname}</p>
                <p className="text-sm text-muted-foreground">
                  {nameOf(plant.speciesId)} · {t('pot.plants.planted', { date: new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(plant.plantedAt)) })}
                </p>
              </div>
              <Button variant="ghost" size="icon" aria-label={t('pot.plants.remove', { name: plant.nickname })} onClick={() => remove.mutate(plant.id)}>
                <Trash2 className="size-4" aria-hidden />
              </Button>
            </li>
          ))}
        </ul>
      </section>

      <form onSubmit={submit} className="grid gap-3 rounded-xl border bg-card p-4 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
        <div className="space-y-2">
          <Label htmlFor="plant-species">{t('pot.plants.species')}</Label>
          <Select value={speciesId} onValueChange={setSpeciesId}>
            <SelectTrigger id="plant-species" className="w-full">
              <SelectValue placeholder={t('pot.plants.species')} />
            </SelectTrigger>
            <SelectContent>
              {species.data.map((s) => (
                <SelectItem key={s.id} value={s.id}>
                  {label(s.id, s.commonName)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="space-y-2">
          <Label htmlFor="plant-nickname">{t('pot.plants.nickname')}</Label>
          <Input id="plant-nickname" value={nickname} onChange={(event) => setNickname(event.target.value)} maxLength={60} required />
        </div>
        <Button type="submit" disabled={!speciesId || nickname.trim().length === 0 || add.isPending}>
          {t('pot.plants.add')}
        </Button>
      </form>
    </div>
  )
}

// ---- requirements (the editor lives in pot-tools) -------------------------------------------------

// ---- controls -------------------------------------------------------------------------------------

function Controls({ pot, onPlanLimit }: { pot: Pot; onPlanLimit: (error: ApiError) => void }) {
  const { t, i18n } = useTranslation()
  const actuators = useActuators(pot.id)
  const audit = useAuditLog(pot.id)
  const tiers = useTiers()
  const setMode = useSetMode(pot.id)
  const run = useRunActuator(pot.id)
  const kill = useKillSwitch(pot.id)
  const status = usePotStatus(pot.id)
  const [notice, setNotice] = useState<string | null>(null)
  const [needsCalibration, setNeedsCalibration] = useState(false)
  const [calibrating, setCalibrating] = useState(false)
  const [calibrated, setCalibrated] = useState(false)
  const soilChannels = [...new Set((status.data?.zones ?? []).flatMap((z) => z.metrics).filter((m) => m.metric === 'soil_moisture' && m.channelId).map((m) => m.channelId!))]

  if (actuators.isError) return <LoadError />
  if (!actuators.data) return <BlockSkeleton className="h-72" />

  const unlockingTier = (metric: Schemas['Metric']) =>
    tiers.data?.find((tier) => tier.limits.automation.some((a) => a.metric === metric && a.maxMode === 'auto'))?.tier
  const fail = (error: unknown) => {
    if (error instanceof ApiError && error.isPlanLimit) onPlanLimit(error)
    else if (error instanceof ApiError && error.code === 'calibration_required') setNeedsCalibration(true)
    else setNotice(errorText(t, error))
  }
  const time = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' })

  return (
    <div className="space-y-6">
      <section aria-labelledby="kill-title" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
        <div>
          <h2 id="kill-title" className="text-lg font-semibold">
            {t('pot.controls.killSwitch')}
          </h2>
          <p className="text-sm text-muted-foreground">{pot.killSwitch ? t('pot.controls.killSwitchOn') : t('pot.controls.killSwitchOff')}</p>
        </div>
        <Button variant={pot.killSwitch ? 'secondary' : 'destructive'} className="gap-1.5" onClick={() => kill.mutate(!pot.killSwitch)}>
          <OctagonX className="size-4" aria-hidden />
          {pot.killSwitch ? t('pot.controls.resume') : t('pot.controls.stopAll')}
        </Button>
      </section>

      {notice && (
        <p role="alert" className="rounded-xl border border-destructive/40 bg-destructive/5 p-3 text-sm font-semibold text-destructive">
          {notice}
        </p>
      )}

      {needsCalibration && soilChannels.length > 0 && (
        <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-attention/60 bg-attention/10 p-4">
          <div>
            <p className="font-semibold">{t('pot.controls.calibrateTitle')}</p>
            <p className="text-sm">{t('pot.controls.calibrateBody')}</p>
          </div>
          <Button size="sm" onClick={() => setCalibrating(true)}>
            {t('pot.controls.calibrate')}
          </Button>
        </div>
      )}
      <CalibrationDialog potId={pot.id} channelIds={soilChannels} open={calibrating} onOpenChange={setCalibrating} onDone={() => { setNeedsCalibration(false); setNotice(null); setCalibrated(true) }} />
      {calibrated && (
        <p role="status" className="text-sm font-semibold text-ok">
          {t('pot.controls.calibrated')}
        </p>
      )}

      {actuators.data.length === 0 && <p className="text-muted-foreground">{t('pot.controls.noActuators')}</p>}
      <div className="grid gap-4 lg:grid-cols-2">
        {actuators.data.map((actuator) => (
          <ActuatorControl
            key={actuator.id}
            actuator={actuator}
            killSwitch={pot.killSwitch}
            requiredTier={unlockingTier(actuator.metric)}
            onModeChange={(mode: ControlMode) => {
              setNotice(null)
              setMode.mutate({ actuatorId: actuator.id, mode }, { onError: fail })
            }}
            onRun={(seconds) => {
              setNotice(null)
              run.mutate({ actuatorId: actuator.id, seconds }, { onSuccess: () => setNotice(null), onError: fail })
            }}
          />
        ))}
      </div>

      <section aria-labelledby="audit-title" className="space-y-2">
        <h2 id="audit-title" className="text-xl font-semibold">
          {t('pot.controls.audit')}
        </h2>
        {audit.data && audit.data.length === 0 && <p className="text-muted-foreground">{t('pot.controls.auditEmpty')}</p>}
        <ul className="space-y-2">
          {audit.data?.map((entry) => (
            <li key={entry.id} className="rounded-xl border bg-card p-3 text-sm">
              <span className="flex flex-wrap items-center gap-2">
                <Badge variant={entry.actor === 'safety' ? 'destructive' : 'secondary'}>{t(`pot.controls.actor.${entry.actor}`)}</Badge>
                <span className="text-muted-foreground">{time.format(new Date(entry.at))}</span>
                {entry.outcome && <Badge variant={entry.outcome === 'completed' ? 'outline' : 'destructive'}>{t(`audit.outcome.${entry.outcome}`)}</Badge>}
              </span>
              <p className="mt-1">{auditText(t, i18n.language, entry.reason)}</p>
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}

// ---- care and devices -----------------------------------------------------------------------------

function Care({ pot }: { pot: Pot }) {
  const { t } = useTranslation()
  const tasks = useCareTasks(pot.id)
  const complete = useCompleteTask(pot.id)
  if (tasks.isError) return <LoadError />
  if (!tasks.data) return <BlockSkeleton />
  const open = tasks.data.filter((task) => task.state === 'open')
  const done = tasks.data.filter((task) => task.state === 'done')

  return (
    <div className="space-y-6">
      <CareForm potId={pot.id} />
      <section aria-labelledby="open-title" className="space-y-2">
        <h2 id="open-title" className="text-xl font-semibold">
          {t('pot.care.open')}
        </h2>
        {open.length === 0 && <p className="text-muted-foreground">{t('pot.care.empty')}</p>}
        {open.map((task) => (
          <CareTaskItem key={task.id} task={task} onComplete={(id) => complete.mutate(id)} />
        ))}
      </section>
      {done.length > 0 && (
        <section aria-labelledby="done-title" className="space-y-2">
          <h2 id="done-title" className="text-xl font-semibold">
            {t('pot.care.done')}
          </h2>
          {done.map((task) => (
            <CareTaskItem key={task.id} task={task} />
          ))}
        </section>
      )}
    </div>
  )
}

function Devices({ pot }: { pot: Pot }) {
  const { t, i18n } = useTranslation()
  const devices = useDevices(pot.id)
  const actuators = useActuators(pot.id)
  const [now] = useState(() => Date.now())
  if (devices.isError) return <LoadError />
  if (!devices.data) return <BlockSkeleton />
  // A thermal cutoff matters for a heater only, so a controller without one is not shown as missing a safety feature.
  const hasHeater = actuators.data?.some((a) => a.kind === 'heater') ?? false
  const when = (iso?: string | null) => {
    if (!iso) return ''
    const seconds = Math.round((new Date(iso).getTime() - now) / 1000)
    if (Math.abs(seconds) < 60) return t('pot.devices.justNow')
    const relative = new Intl.RelativeTimeFormat(i18n.language, { numeric: 'auto' })
    if (Math.abs(seconds) < 3600) return relative.format(Math.round(seconds / 60), 'minute')
    if (Math.abs(seconds) < 86400) return relative.format(Math.round(seconds / 3600), 'hour')
    return relative.format(Math.round(seconds / 86400), 'day')
  }

  return (
    <section aria-labelledby="devices-title" className="space-y-3">
      <h2 id="devices-title" className="text-xl font-semibold">
        {t('pot.devices.title')}
      </h2>
      {devices.data.length === 0 && <p className="text-muted-foreground">{t('pot.devices.empty')}</p>}
      <ul className="space-y-2">
        {devices.data.map((device) => (
          <li key={device.deviceId} className="flex flex-wrap items-center justify-between gap-2 rounded-xl border bg-card p-3">
            <div>
              <p className="font-semibold">{device.model}</p>
              <p className="text-sm text-muted-foreground">
                {device.deviceId}
                {device.firmware ? ` · ${t('pot.devices.firmware', { version: device.firmware })}` : ''}
                {device.lastSeenAt ? ` · ${t('pot.devices.lastSeen', { when: when(device.lastSeenAt) })}` : ''}
              </p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant={device.status === 'online' ? 'secondary' : 'destructive'}>
                {device.status === 'online' ? t('pot.devices.online') : t('pot.devices.offline')}
              </Badge>
              {(hasHeater || device.thermalCutoffDeclared) && <Badge variant="outline">{device.thermalCutoffDeclared ? t('pot.devices.cutoffYes') : t('pot.devices.cutoffNo')}</Badge>}
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

// ---- page -----------------------------------------------------------------------------------------

export function PotPage() {
  const { t } = useTranslation()
  const { potId = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const pot = usePot(potId)
  const status = usePotStatus(potId)
  const [upsell, setUpsell] = useState<ApiError | null>(null)
  const tab = (tabs as readonly string[]).includes(params.get('tab') ?? '') ? (params.get('tab') as string) : 'overview'
  // On a phone the tab list scrolls, so the tab that opens (also from a link) is brought into view.
  const loaded = pot.data !== undefined
  useEffect(() => {
    if (loaded) document.querySelector('[role="tab"][data-state="active"]')?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }, [tab, loaded])

  if (pot.isError) return <LoadError onRetry={() => void pot.refetch()} />
  if (!pot.data) return <BlockSkeleton className="h-96" />

  return (
    <div className="space-y-6">
      <div>
        <Link to="/" className="mb-2 inline-flex items-center gap-1 text-sm font-semibold text-muted-foreground hover:text-foreground">
          <ArrowLeft className="size-4" aria-hidden />
          {t('pot.back')}
        </Link>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-3xl font-semibold">{pot.data.name}</h1>
            <p className="text-muted-foreground">{[t(`pot.kind.${pot.data.kind}`), pot.data.location].filter(Boolean).join(' · ')}</p>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            {status.data && <StatusChip state={status.data.state} />}
            <PotActions pot={pot.data} />
          </div>
        </div>
      </div>

      <Tabs value={tab} onValueChange={(value) => setParams({ tab: value })}>
        <TabsList className="h-auto max-w-full justify-start overflow-x-auto [&>*]:shrink-0">
          {tabs.map((name) => (
            <TabsTrigger key={name} value={name}>
              {t(`pot.tabs.${name}`)}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview" className="pt-4">
          <Overview pot={pot.data} onPlanLimit={setUpsell} />
        </TabsContent>
        <TabsContent value="plants" className="pt-4">
          <Plants pot={pot.data} />
        </TabsContent>
        <TabsContent value="requirements" className="pt-4">
          <RequirementsTab pot={pot.data} />
        </TabsContent>
        <TabsContent value="controls" className="pt-4">
          <Controls pot={pot.data} onPlanLimit={setUpsell} />
        </TabsContent>
        <TabsContent value="care" className="pt-4">
          <Care pot={pot.data} />
        </TabsContent>
        <TabsContent value="devices" className="pt-4">
          <Devices pot={pot.data} />
        </TabsContent>
        <TabsContent value="assistant" className="pt-4">
          <AssistantTab potId={pot.data.id} onPlanLimit={setUpsell} />
        </TabsContent>
      </Tabs>

      <UpsellDialog error={upsell} onClose={() => setUpsell(null)} />
    </div>
  )
}
