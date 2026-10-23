import { AssistantPanel } from '@greenhaus/ui'
import { useState } from 'react'
import { useAssistant } from '~/api/assistant'
import { useEntitlements } from '~/api/hooks'
import type { ApiError } from '~/api/http'
import { BlockSkeleton, LoadError } from '~/components/feedback'

/** The assistant of one pot: the quota and what the plan lets it see come from the entitlements, the answer from the stream. */
export function AssistantTab({ potId, onPlanLimit }: { potId: string; onPlanLimit: (error: ApiError) => void }) {
  const entitlements = useEntitlements()
  const assistant = useAssistant(potId, onPlanLimit)
  const [question, setQuestion] = useState('')

  if (entitlements.isError) return <LoadError onRetry={() => void entitlements.refetch()} />
  if (!entitlements.data) return <BlockSkeleton className="h-64" />

  const { limits, usage } = entitlements.data
  return (
    <AssistantPanel
      status={assistant.status}
      question={question}
      onQuestionChange={setQuestion}
      onAsk={(text) => void assistant.ask(text)}
      answer={assistant.answer.text}
      sources={assistant.answer.sources}
      fallback={assistant.answer.mode === 'retrieval_only' || assistant.answer.mode === 'no_results' ? assistant.answer.mode : undefined}
      dataAccess={limits.aiDataAccess}
      used={usage.aiQuestionsThisMonth}
      limit={limits.aiQuestionsPerMonth}
      onRetry={assistant.retry}
    />
  )
}
