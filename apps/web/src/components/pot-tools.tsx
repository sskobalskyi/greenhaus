import {
  Button,
  CalibrationForm,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  Input,
  Label,
  RequirementsEditor,
  type Schemas,
} from '@greenhaus/ui'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router'
import { useCalibrate, useCreateTask, useDeletePot, useRequirements, useSetRequirements, useSuggestion, useUpdatePot } from '~/api/hooks'
import { BlockSkeleton, LoadError } from '~/components/feedback'

type Pot = Schemas['Pot']

/** The editable targets of a pot, with what its plants suggest. */
export function RequirementsTab({ pot }: { pot: Pot }) {
  const { t } = useTranslation()
  const requirements = useRequirements(pot.id)
  const suggestion = useSuggestion(pot.id)
  const save = useSetRequirements(pot.id)
  if (requirements.isError) return <LoadError />
  if (!requirements.data) return <BlockSkeleton className="h-96" />

  return (
    <div className="space-y-4">
      <p className="text-muted-foreground">{t('pot.requirements.intro')}</p>
      <RequirementsEditor requirements={requirements.data} suggestion={suggestion.data} saving={save.isPending} onSave={(draft) => save.mutate(draft)} className="max-w-2xl" />
      {save.isSuccess && (
        <p role="status" className="text-sm font-semibold text-ok">
          {t('pot.requirements.saved')}
        </p>
      )}
      {save.isError && (
        <p role="alert" className="text-sm font-semibold text-destructive">
          {t('pot.requirements.failed')}
        </p>
      )}
    </div>
  )
}

/** Asks for the two raw readings and calibrates the soil moisture sensors of the pot. */
export function CalibrationDialog({ potId, channelIds, open, onOpenChange, onDone }: { potId: string; channelIds: string[]; open: boolean; onOpenChange: (open: boolean) => void; onDone: () => void }) {
  const { t } = useTranslation()
  const calibrate = useCalibrate(potId)
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{t('calibration.title')}</DialogTitle>
          <DialogDescription>{t('pot.controls.calibrateBody')}</DialogDescription>
        </DialogHeader>
        <CalibrationForm
          sensorCount={channelIds.length}
          saving={calibrate.isPending}
          onSubmit={({ dryRaw, wetRaw, applyToAll }) =>
            calibrate.mutate(
              { channelIds: applyToAll ? channelIds : channelIds.slice(0, 1), dryRaw, wetRaw },
              {
                onSuccess: () => {
                  onOpenChange(false)
                  onDone()
                },
              },
            )
          }
        />
        {calibrate.isError && (
          <p role="alert" className="text-sm font-semibold text-destructive">
            {t('app.errors.unknown')}
          </p>
        )}
      </DialogContent>
    </Dialog>
  )
}

/** Adds a custom care task: a title, a due date and, if wanted, a repeat. */
export function CareForm({ potId }: { potId: string }) {
  const { t } = useTranslation()
  const create = useCreateTask(potId)
  const [title, setTitle] = useState('')
  const [due, setDue] = useState('')
  const [repeat, setRepeat] = useState('')

  const submit = (event: FormEvent) => {
    event.preventDefault()
    create.mutate(
      { title: title.trim(), dueAt: new Date(`${due}T08:00:00`).toISOString(), repeatEveryDays: repeat === '' ? null : Number(repeat) },
      {
        onSuccess: () => {
          setTitle('')
          setDue('')
          setRepeat('')
        },
      },
    )
  }

  return (
    <form onSubmit={submit} className="grid gap-3 rounded-xl border bg-card p-4 sm:grid-cols-[2fr_1fr_1fr_auto] sm:items-end" aria-label={t('pot.care.new')}>
      <div className="space-y-2">
        <Label htmlFor="task-title">{t('pot.care.taskTitle')}</Label>
        <Input id="task-title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={80} required />
      </div>
      <div className="space-y-2">
        <Label htmlFor="task-due">{t('pot.care.due')}</Label>
        <Input id="task-due" type="date" value={due} onChange={(e) => setDue(e.target.value)} required />
      </div>
      <div className="space-y-2">
        <Label htmlFor="task-repeat">{t('pot.care.repeat')}</Label>
        <Input id="task-repeat" type="number" min={1} value={repeat} onChange={(e) => setRepeat(e.target.value)} />
      </div>
      <Button type="submit" disabled={create.isPending || title.trim().length === 0 || due === ''} className="gap-1.5">
        <Plus className="size-4" aria-hidden />
        {t('pot.care.add')}
      </Button>
    </form>
  )
}

/** Rename and delete a pot. */
export function PotActions({ pot }: { pot: Pot }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const update = useUpdatePot(pot.id)
  const remove = useDeletePot(pot.id)
  const [renaming, setRenaming] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [name, setName] = useState(pot.name)

  return (
    <div className="flex items-center gap-2">
      <Dialog open={renaming} onOpenChange={setRenaming}>
        <DialogTrigger asChild>
          <Button variant="outline" size="sm" className="gap-1.5" onClick={() => setName(pot.name)}>
            <Pencil className="size-4" aria-hidden />
            {t('pot.rename.action')}
          </Button>
        </DialogTrigger>
        <DialogContent>
          <form
            className="space-y-4"
            onSubmit={(event) => {
              event.preventDefault()
              update.mutate({ name: name.trim() }, { onSuccess: () => setRenaming(false) })
            }}
          >
            <DialogHeader>
              <DialogTitle>{t('pot.rename.title')}</DialogTitle>
            </DialogHeader>
            <div className="space-y-2">
              <Label htmlFor="rename-name">{t('pot.rename.name')}</Label>
              <Input id="rename-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={60} required autoFocus />
            </div>
            <DialogFooter>
              <Button type="submit" disabled={update.isPending || name.trim().length === 0}>
                {t('app.save')}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={deleting} onOpenChange={setDeleting}>
        <DialogTrigger asChild>
          <Button variant="outline" size="sm" className="gap-1.5 text-destructive">
            <Trash2 className="size-4" aria-hidden />
            {t('pot.delete.action')}
          </Button>
        </DialogTrigger>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('pot.delete.title', { name: pot.name })}</DialogTitle>
            <DialogDescription>{t('pot.delete.body')}</DialogDescription>
          </DialogHeader>
          <DialogFooter className="gap-2">
            <Button variant="ghost" onClick={() => setDeleting(false)}>
              {t('app.cancel')}
            </Button>
            <Button variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate(undefined, { onSuccess: () => void navigate('/') })}>
              {t('pot.delete.confirm')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
