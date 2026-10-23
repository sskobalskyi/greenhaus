import { apiBase, createSseParser, type Tier } from '@greenhaus/ui'
import { accessToken } from '~/auth/auth'

/** A problem document from the API, with the stable code the interface translates. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly limitType?: string
  readonly requiredTier?: Tier

  constructor(status: number, code: string, title: string, limitType?: string, requiredTier?: Tier) {
    super(title)
    this.status = status
    this.code = code
    this.limitType = limitType
    this.requiredTier = requiredTier
  }

  /** The request was refused because of the plan, not because it was wrong. */
  get isPlanLimit(): boolean {
    return this.code === 'limit_reached' || this.code === 'tier_required'
  }
}

/** Only the mock reads this header. It lets a developer try every tier without real accounts. */
const mockEnabled = import.meta.env.VITE_MOCK !== 'false'

export function mockTier(): Tier | null {
  if (!mockEnabled) return null
  try {
    const stored = localStorage.getItem('mockTier')
    return stored === 'novice' || stored === 'gardener' || stored === 'druid' ? stored : null
  } catch {
    return null
  }
}

export function setMockTier(tier: Tier | null): void {
  try {
    if (tier) localStorage.setItem('mockTier', tier)
    else localStorage.removeItem('mockTier')
  } catch {
    /* storage may be unavailable */
  }
}

/** The headers every call carries: the mock tier (mock only) and the person's bearer token. */
async function requestHeaders(json: boolean): Promise<Record<string, string>> {
  const headers: Record<string, string> = {}
  const tier = mockTier()
  if (tier) headers['X-Mock-Tier'] = tier
  const token = await accessToken()
  if (token) headers.Authorization = `Bearer ${token}`
  if (json) headers['Content-Type'] = 'application/json'
  return headers
}

/** Turns a refused response into the error the interface translates. */
async function refusal(response: Response): Promise<ApiError> {
  const problem = (await response.json().catch(() => null)) as
    | { code?: string; title?: string; limitType?: string; requiredTier?: Tier }
    | null
  return new ApiError(response.status, problem?.code ?? 'unknown', problem?.title ?? response.statusText, problem?.limitType, problem?.requiredTier)
}

export async function api<T>(path: string, options: { method?: string; json?: unknown; base?: string } = {}): Promise<T> {
  const response = await fetch(`${options.base ?? apiBase}${path}`, {
    method: options.method ?? 'GET',
    headers: await requestHeaders(options.json !== undefined),
    body: options.json === undefined ? undefined : JSON.stringify(options.json),
  })

  if (!response.ok) throw await refusal(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/**
 * A POST whose answer is a stream of server-sent events (EventSource cannot POST, and could not carry the token).
 * Calls `onData` with the data of every event as it arrives, and returns when the stream ends.
 */
export async function apiStream(path: string, options: { json: unknown; onData: (data: string) => void; signal?: AbortSignal }): Promise<void> {
  const response = await fetch(`${apiBase}${path}`, {
    method: 'POST',
    headers: { ...(await requestHeaders(true)), Accept: 'text/event-stream' },
    body: JSON.stringify(options.json),
    signal: options.signal,
  })
  if (!response.ok) throw await refusal(response)
  if (!response.body) throw new ApiError(502, 'assistant_unavailable', 'The answer did not come as a stream')

  const parser = createSseParser(options.onData)
  // The decoder keeps a character that is cut between two chunks, which matters for Ukrainian text.
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    parser.push(value)
  }
  parser.end()
}

/** Base URL of the billing API, which has its own mock. Prices are optional until it is running. */
export const billingBase: string =
  (import.meta.env.VITE_BILLING_BASE as string | undefined) ?? 'http://localhost:8090/api/billing/v1'

/** Where the marketplace lives. Locally this is Orderflow's web app. */
export const shopUrl: string = (import.meta.env.VITE_SHOP_URL as string | undefined) ?? 'http://localhost:5173'
