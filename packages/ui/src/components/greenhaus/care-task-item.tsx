import { CheckCircle2, FlaskConical, Pill, Settings2, Sprout, type LucideIcon } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import type { Schemas } from '@/api/client'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

const kindIcon: Record<Schemas['CareTask']['kind'], LucideIcon> = {
  fertilize: Sprout,
  check_pump: Settings2,
  ph_check: FlaskConical,
  custom: Pill,
}

export interface CareTaskItemProps {
  task: Schemas['CareTask']
  onComplete?: (taskId: string) => void
  className?: string
}

export function CareTaskItem({ task, onComplete, className }: CareTaskItemProps) {
  const { t, i18n } = useTranslation()
  const Icon = kindIcon[task.kind]
  const done = task.state === 'done'
  const format = (iso: string) => new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' }).format(new Date(iso))

  return (
    <div className={cn('flex items-center gap-3 rounded-xl border bg-card p-3', className)}>
      <span className="flex size-10 shrink-0 items-center justify-center rounded-full bg-secondary text-secondary-foreground">
        <Icon className="size-5" aria-hidden />
      </span>
      <div className="min-w-0 flex-1">
        <p className={cn('truncate font-semibold', done && 'line-through')}>{task.title}</p>
        <p className="text-sm text-muted-foreground">
          {done && task.completedAt ? t('care.completed', { date: format(task.completedAt) }) : t('care.due', { date: format(task.dueAt) })}
        </p>
        {task.origin && (
          <Badge variant="outline" className="mt-1">
            {t(`care.origin.${task.origin}`)}
          </Badge>
        )}
      </div>
      {done ? (
        <CheckCircle2 className="size-6 shrink-0 text-ok" aria-label={t('care.markDone')} />
      ) : (
        <Button size="sm" variant="secondary" onClick={() => onComplete?.(task.id)}>
          {t('care.markDone')}
        </Button>
      )}
    </div>
  )
}
