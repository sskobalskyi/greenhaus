import type { Schemas } from '@/api/client'

/**
 * Reads server-sent events from chunks of text that may end anywhere, even inside a line or between the two
 * characters of a line break. Calls `onData` with the joined `data:` lines of every complete event. Comment
 * lines (starting with a colon) and other fields are ignored.
 */
export function createSseParser(onData: (data: string) => void) {
  let pending = ''
  let lines: string[] = []

  const flush = () => {
    if (lines.length > 0) onData(lines.join('\n'))
    lines = []
  }

  const readLine = (line: string) => {
    if (line === '') return flush()
    if (line.startsWith(':')) return
    const colon = line.indexOf(':')
    const field = colon === -1 ? line : line.slice(0, colon)
    if (field !== 'data') return
    const value = colon === -1 ? '' : line.slice(colon + 1)
    lines.push(value.startsWith(' ') ? value.slice(1) : value)
  }

  return {
    push(chunk: string) {
      pending += chunk
      // A lone carriage return at the very end may be the first half of CRLF, so keep it for the next chunk.
      const complete = pending.endsWith('\r') ? pending.slice(0, -1) : pending
      const rest = pending.endsWith('\r') ? '\r' : ''
      const parts = complete.split(/\r\n|\n|\r/)
      pending = parts.pop()! + rest
      for (const line of parts) readLine(line)
    },
    /** The stream is over: a last line without its line break still counts, and so does an event that was not closed. */
    end() {
      if (pending !== '') readLine(pending.replace(/\r$/, ''))
      pending = ''
      flush()
    },
  }
}

export type AssistantEvent = Schemas['AssistantEvent']
export type AssistantSource = NonNullable<AssistantEvent['sources']>[number]

export interface AssistantAnswer {
  text: string
  sources: AssistantSource[]
  /** Set by the `done` event. */
  mode?: NonNullable<AssistantEvent['mode']>
  reason?: string
  tokens?: number
  done: boolean
}

export const emptyAnswer: AssistantAnswer = { text: '', sources: [], done: false }

/** Folds one event of the stream into the answer so far. */
export function applyAssistantEvent(answer: AssistantAnswer, event: AssistantEvent): AssistantAnswer {
  switch (event.type) {
    case 'sources':
      return { ...answer, sources: event.sources ?? [] }
    case 'token':
      return { ...answer, text: answer.text + (event.text ?? '') }
    case 'done':
      return { ...answer, done: true, mode: event.mode, reason: event.reason, tokens: event.tokens }
    default:
      return answer
  }
}
