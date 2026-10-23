import { Info, Send, TriangleAlert } from 'lucide-react'
import { useId, type FormEvent, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import type { Schemas } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import type { AssistantSource } from '@/lib/sse'
import { cn } from '@/lib/utils'
import { QuotaMeter } from './quota-meter'

export const ASSISTANT_QUESTION_MAX = 500

export type AssistantStatus =
  /** Nothing asked yet. */
  | 'idle'
  /** The answer is arriving. */
  | 'asking'
  /** A full answer from the assistant. */
  | 'answered'
  /** Passages only, or nothing found: the assistant did not write an answer. */
  | 'fallback'
  | 'error'
  | 'unavailable'
  /** The last question was refused because the monthly questions are used up (the lock itself follows used and limit). */
  | 'limit'

export interface AssistantPanelProps {
  status: AssistantStatus
  question: string
  onQuestionChange: (question: string) => void
  onAsk: (question: string) => void
  /** The answer so far, growing while `status` is `asking`. */
  answer?: string
  sources?: AssistantSource[]
  /** Which kind of fallback it was, with `status` `fallback`. */
  fallback?: 'retrieval_only' | 'no_results'
  /** What the plan lets the assistant see of the pot. */
  dataAccess: Schemas['Limits']['aiDataAccess']
  used: number
  limit: number
  onRetry?: () => void
  className?: string
}

/** Ask the plant assistant about a pot and read the answer as it is written. Props only: the data comes from the app. */
export function AssistantPanel({
  status,
  question,
  onQuestionChange,
  onAsk,
  answer = '',
  sources = [],
  fallback,
  dataAccess,
  used,
  limit,
  onRetry,
  className,
}: AssistantPanelProps) {
  const { t } = useTranslation()
  const id = useId()
  const asking = status === 'asking'
  // The quota decides, not the last refusal, so a plan change that raises the limit unlocks the input at once.
  const exhausted = used >= limit
  const trimmed = question.trim()
  const canAsk = trimmed.length > 0 && !asking && !exhausted

  const submit = (event?: FormEvent) => {
    event?.preventDefault()
    if (canAsk) onAsk(trimmed)
  }
  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) submit()
  }

  const showAnswer = asking || status === 'answered' || status === 'fallback'

  return (
    <section aria-labelledby={`${id}-title`} className={cn('space-y-4', className)}>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0 flex-1 space-y-1">
          <h2 id={`${id}-title`} className="text-xl font-semibold">
            {t('assistant.title')}
          </h2>
          <p className="flex items-start gap-2 text-sm text-muted-foreground">
            <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
            {t(`assistant.data.${dataAccess}`)}
          </p>
        </div>
        <QuotaMeter used={used} limit={limit} className="w-full sm:w-64" />
      </div>

      <form onSubmit={submit} className="space-y-2">
        <Label htmlFor={`${id}-question`}>{t('assistant.label')}</Label>
        <Textarea
          id={`${id}-question`}
          value={question}
          maxLength={ASSISTANT_QUESTION_MAX}
          placeholder={t('assistant.placeholder')}
          disabled={asking || exhausted}
          aria-describedby={`${id}-counter`}
          onChange={(event) => onQuestionChange(event.target.value)}
          onKeyDown={onKeyDown}
        />
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p id={`${id}-counter`} className="text-sm text-muted-foreground">
            {t('assistant.counter', { count: question.length, max: ASSISTANT_QUESTION_MAX })}
          </p>
          <Button type="submit" disabled={!canAsk}>
            <Send className="size-4" aria-hidden />
            {asking ? t('assistant.asking') : t('assistant.ask')}
          </Button>
        </div>
      </form>

      {exhausted && (
        <p role="note" className="flex items-start gap-2 rounded-xl border border-attention/60 bg-attention/10 p-3 text-sm">
          <TriangleAlert className="mt-0.5 size-4 shrink-0 text-attention-ink" aria-hidden />
          {t('assistant.limit', { limit })}
        </p>
      )}

      {(status === 'error' || status === 'unavailable') && (
        <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-destructive/50 bg-destructive/10 p-3 text-sm">
          <p>{t(status === 'error' ? 'assistant.error' : 'assistant.unavailable')}</p>
          {onRetry && (
            <Button type="button" variant="outline" size="sm" onClick={onRetry}>
              {t('assistant.retry')}
            </Button>
          )}
        </div>
      )}

      {/* A screen reader announces the answer politely as it is written. */}
      <div aria-live="polite" aria-busy={asking} className={cn(showAnswer && 'space-y-3 rounded-xl border bg-card p-4')}>
        {showAnswer && (
          <>
            <h3 className="text-sm font-semibold uppercase tracking-wide text-muted-foreground">{t('assistant.answer')}</h3>
            {status === 'fallback' && fallback && <p className="text-sm text-muted-foreground">{t(`assistant.fallback.${fallback}`)}</p>}
            {answer ? (
              <p className="whitespace-pre-wrap">{answer}</p>
            ) : (
              asking && <p className="text-muted-foreground">{t('assistant.thinking')}</p>
            )}
            {sources.length > 0 && (
              <div className="space-y-2">
                <h4 className="text-sm font-semibold">{t('assistant.sources')}</h4>
                <ol className="space-y-2">
                  {sources.map((source) => (
                    <li key={source.number} className="rounded-lg border bg-secondary/40 p-3 text-sm">
                      <p className="font-semibold">
                        <span className="mr-2 inline-flex size-5 items-center justify-center rounded-full bg-primary text-xs text-primary-foreground" aria-label={t('assistant.source', { number: source.number })}>
                          {source.number}
                        </span>
                        {source.title}
                      </p>
                      <p className="mt-1 text-muted-foreground">{source.text}</p>
                    </li>
                  ))}
                </ol>
              </div>
            )}
          </>
        )}
      </div>
    </section>
  )
}
