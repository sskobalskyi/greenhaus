import { Button, Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle, DialogTrigger, Input, Label, PotCard, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, type Schemas } from '@greenhaus/ui'
import { Plus } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { useCreatePot, usePlants, usePotStatus, usePots, useRequirements, useZones } from '~/api/hooks'
import { ApiError } from '~/api/http'
import { BlockSkeleton, CardGridSkeleton, LoadError } from '~/components/feedback'
import { UpsellDialog } from '~/components/upsell-dialog'

function PotTile({ pot }: { pot: Schemas['Pot'] }) {
  const status = usePotStatus(pot.id)
  const requirements = useRequirements(pot.id)
  const zones = useZones(pot.id)
  const plants = usePlants(pot.id)

  if (status.isError || requirements.isError || zones.isError || plants.isError) return <LoadError />
  if (!status.data || !requirements.data || !zones.data || !plants.data) return <BlockSkeleton className="h-64" />
  return (
    <Link to={`/pots/${pot.id}`} className="block rounded-xl outline-none focus-visible:ring-4 focus-visible:ring-ring/50">
      <PotCard pot={pot} status={status.data} requirements={requirements.data} zones={zones.data} plantCount={plants.data.length} className="h-full transition-shadow hover:shadow-md" />
    </Link>
  )
}

function AddPotDialog({ onPlanLimit }: { onPlanLimit: (error: ApiError) => void }) {
  const { t } = useTranslation()
  const create = useCreatePot()
  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [kind, setKind] = useState<Schemas['Pot']['kind']>('pot')

  const submit = (event: FormEvent) => {
    event.preventDefault()
    create.mutate(
      { name: name.trim(), kind },
      {
        onSuccess: () => {
          setOpen(false)
          setName('')
        },
        onError: (error) => {
          if (error instanceof ApiError && error.isPlanLimit) {
            setOpen(false)
            onPlanLimit(error)
          }
        },
      },
    )
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button className="gap-1.5">
          <Plus className="size-4" aria-hidden />
          {t('pots.add')}
        </Button>
      </DialogTrigger>
      <DialogContent>
        <form onSubmit={submit} className="space-y-4">
          <DialogHeader>
            <DialogTitle>{t('pots.add')}</DialogTitle>
          </DialogHeader>
          <div className="space-y-2">
            <Label htmlFor="pot-name">{t('pots.name')}</Label>
            <Input id="pot-name" value={name} onChange={(event) => setName(event.target.value)} required maxLength={60} autoFocus />
          </div>
          <div className="space-y-2">
            <Label htmlFor="pot-kind">{t('pots.kind')}</Label>
            <Select value={kind} onValueChange={(value) => setKind(value as Schemas['Pot']['kind'])}>
              <SelectTrigger id="pot-kind" className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {(['pot', 'balcony', 'bed'] as const).map((k) => (
                  <SelectItem key={k} value={k}>
                    {t(`pot.kind.${k}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          {create.error && !(create.error instanceof ApiError && create.error.isPlanLimit) && (
            <p role="alert" className="text-sm text-destructive">
              {t('app.errors.unknown')}
            </p>
          )}
          <DialogFooter>
            <Button type="submit" disabled={create.isPending || name.trim().length === 0}>
              {t('pots.create')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

export function PotsPage() {
  const { t } = useTranslation()
  const pots = usePots()
  const [upsell, setUpsell] = useState<ApiError | null>(null)

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-3xl font-semibold">{t('pots.title')}</h1>
          <p className="text-muted-foreground">{t('pots.subtitle')}</p>
        </div>
        <AddPotDialog onPlanLimit={setUpsell} />
      </div>

      {pots.isError && <LoadError onRetry={() => void pots.refetch()} />}
      {pots.isPending && <CardGridSkeleton />}
      {pots.data && pots.data.length === 0 && <p className="text-muted-foreground">{t('pots.empty')}</p>}
      {pots.data && pots.data.length > 0 && (
        <ul className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {pots.data.map((pot) => (
            <li key={pot.id}>
              <PotTile pot={pot} />
            </li>
          ))}
        </ul>
      )}

      <UpsellDialog error={upsell} onClose={() => setUpsell(null)} />
    </div>
  )
}
