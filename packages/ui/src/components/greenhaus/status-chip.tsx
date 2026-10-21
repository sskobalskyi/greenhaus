import { Check, CircleHelp, TriangleAlert, WifiOff, type LucideIcon } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import type { ReadingState } from '@/api/client'
import { cn } from '@/lib/utils'

// Shape and colour together, so the state does not rely on colour alone.
const look: Record<ReadingState, { icon: LucideIcon; dot: string; chip: string }> = {
  ok: { icon: Check, dot: 'bg-ok', chip: 'bg-ok/15' },
  attention: { icon: TriangleAlert, dot: 'bg-attention', chip: 'bg-attention/20' },
  offline: { icon: WifiOff, dot: 'bg-offline', chip: 'bg-offline/15' },
  unknown: { icon: CircleHelp, dot: 'bg-offline', chip: 'bg-muted' },
}

export function StatusChip({ state, className }: { state: ReadingState; className?: string }) {
  const { t } = useTranslation()
  const { icon: Icon, dot, chip } = look[state]
  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold whitespace-nowrap text-foreground',
        chip,
        className,
      )}
    >
      <span className={cn('flex size-4 items-center justify-center rounded-full text-background', dot)}>
        <Icon className="size-3" strokeWidth={3} aria-hidden />
      </span>
      {t(`status.${state}`)}
    </span>
  )
}
