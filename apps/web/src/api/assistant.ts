import { applyAssistantEvent, emptyAnswer, type AssistantAnswer, type AssistantEvent, type AssistantStatus } from '@greenhaus/ui'
import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useRef, useState } from 'react'
import { key } from './hooks'
import { ApiError, apiStream } from './http'

/**
 * Asking the assistant about a pot. The answer streams in, so the state follows it: asking while it arrives, then
 * answered (the model wrote it) or fallback (passages only, or nothing found). A refusal for the monthly quota becomes
 * `limit` and is passed on, so the page can show the plan that has more.
 */
export function useAssistant(potId: string, onPlanLimit: (error: ApiError) => void) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<AssistantStatus>('idle')
  const [answer, setAnswer] = useState<AssistantAnswer>(emptyAnswer)
  const last = useRef('')
  const controller = useRef<AbortController | null>(null)

  // Leaving the page or the pot stops an answer that is still arriving.
  useEffect(() => () => controller.current?.abort(), [potId])

  const ask = useCallback(
    async (question: string) => {
      controller.current?.abort()
      const mine = new AbortController()
      controller.current = mine
      last.current = question
      setStatus('asking')
      setAnswer(emptyAnswer)

      let current = emptyAnswer
      try {
        await apiStream(`/pots/${potId}/assistant/ask`, {
          json: { question },
          signal: mine.signal,
          onData: (data) => {
            try {
              current = applyAssistantEvent(current, JSON.parse(data) as AssistantEvent)
            } catch {
              return // a line that is not an event is not worth stopping for
            }
            setAnswer(current)
          },
        })
        if (mine.signal.aborted) return
        // A stream that ends without its last event was cut short.
        if (!current.done) throw new ApiError(502, 'assistant_unavailable', 'The answer stopped early')
        setStatus(current.mode === 'answer' ? 'answered' : 'fallback')
        // Only answered questions count, so the quota is read again from the service.
        await queryClient.invalidateQueries({ queryKey: key('entitlements') })
      } catch (error) {
        if (mine.signal.aborted) return
        if (error instanceof ApiError && error.code === 'limit_reached' && error.limitType === 'ai_questions') {
          setStatus('limit')
          onPlanLimit(error)
          void queryClient.invalidateQueries({ queryKey: key('entitlements') })
        } else if (error instanceof ApiError && (error.status === 503 || error.code === 'assistant_unavailable')) {
          setStatus('unavailable')
        } else {
          setStatus('error')
        }
      }
    },
    [potId, onPlanLimit, queryClient],
  )

  return { status, answer, ask, retry: () => void ask(last.current) }
}
