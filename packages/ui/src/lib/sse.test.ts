import { describe, expect, it } from 'vitest'
import { applyAssistantEvent, createSseParser, emptyAnswer, type AssistantEvent } from './sse'

function parse(chunks: string[]): string[] {
  const events: string[] = []
  const parser = createSseParser((data) => events.push(data))
  for (const chunk of chunks) parser.push(chunk)
  parser.end()
  return events
}

const stream = 'data: {"type":"sources","sources":[]}\n\ndata: {"type":"token","text":"Hi "}\n\ndata: {"type":"done","mode":"answer","tokens":2}\n\n'
const expected = ['{"type":"sources","sources":[]}', '{"type":"token","text":"Hi "}', '{"type":"done","mode":"answer","tokens":2}']

describe('createSseParser', () => {
  it('reads whole events', () => {
    expect(parse([stream])).toEqual(expected)
  })

  it('gives the same events however the text is cut into chunks', () => {
    for (let cut = 1; cut < stream.length; cut++) {
      expect(parse([stream.slice(0, cut), stream.slice(cut)])).toEqual(expected)
    }
    // One character at a time is the worst case.
    expect(parse([...stream])).toEqual(expected)
  })

  it('understands CRLF line breaks, also when they are split between chunks', () => {
    const crlf = stream.replaceAll('\n', '\r\n')
    expect(parse([crlf])).toEqual(expected)
    expect(parse([...crlf])).toEqual(expected)
  })

  it('joins the data lines of one event with a line break', () => {
    expect(parse(['data: one\ndata: two\n\n'])).toEqual(['one\ntwo'])
  })

  it('ignores comments, other fields and a missing space after the colon', () => {
    expect(parse([': keep-alive\nevent: message\nid: 7\ndata:tight\n\n'])).toEqual(['tight'])
  })

  it('counts a last event that the stream ended without closing', () => {
    expect(parse(['data: late'])).toEqual(['late'])
    expect(parse(['data: late\n'])).toEqual(['late'])
  })

  it('sends nothing for an empty stream or blank lines', () => {
    expect(parse([''])).toEqual([])
    expect(parse(['\n\n\n'])).toEqual([])
  })
})

describe('applyAssistantEvent', () => {
  const run = (events: AssistantEvent[]) => events.reduce(applyAssistantEvent, emptyAnswer)

  it('builds the answer from sources, tokens and the final event', () => {
    const answer = run([
      { type: 'sources', sources: [{ number: 1, title: 'Basil care', text: 'Water when dry.' }] },
      { type: 'token', text: 'Water ' },
      { type: 'token', text: 'it.' },
      { type: 'done', mode: 'answer', tokens: 2 },
    ])
    expect(answer).toMatchObject({ text: 'Water it.', done: true, mode: 'answer', tokens: 2 })
    expect(answer.sources).toHaveLength(1)
  })

  it('keeps the reason of a fallback', () => {
    const answer = run([{ type: 'done', mode: 'retrieval_only', reason: 'budget_exceeded' }])
    expect(answer).toMatchObject({ done: true, mode: 'retrieval_only', reason: 'budget_exceeded' })
  })

  it('is not done before the final event', () => {
    expect(run([{ type: 'token', text: 'Wa' }]).done).toBe(false)
  })
})
