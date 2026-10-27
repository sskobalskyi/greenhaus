import { Button, Skeleton } from '@greenhaus/ui'
import { TriangleAlert } from 'lucide-react'
import { useTranslation } from 'react-i18next'

export function LoadError({ onRetry }: { onRetry?: () => void }) {
  const { t } = useTranslation()
  return (
    <div role="alert" className="flex flex-col items-start gap-3 rounded-xl border border-destructive/40 bg-destructive/5 p-4">
      <p className="flex items-center gap-2 font-semibold text-destructive">
        <TriangleAlert className="size-5" aria-hidden />
        {import.meta.env.VITE_MOCK === 'false' ? t('app.loadFailed') : t('app.loadFailedMock')}
      </p>
      {onRetry && (
        <Button variant="outline" size="sm" onClick={onRetry}>
          {t('app.retry')}
        </Button>
      )}
    </div>
  )
}

export function CardGridSkeleton({ count = 3 }: { count?: number }) {
  return (
    <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3" aria-hidden>
      {Array.from({ length: count }, (_, i) => (
        <Skeleton key={i} className="h-64 rounded-xl" />
      ))}
    </div>
  )
}

export function BlockSkeleton({ className = 'h-40' }: { className?: string }) {
  return <Skeleton className={`${className} w-full rounded-xl`} aria-hidden />
}
