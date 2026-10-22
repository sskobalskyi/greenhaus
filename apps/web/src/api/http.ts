import { apiBase, type Tier } from '@greenhaus/ui'
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

export async function api<T>(path: string, options: { method?: string; json?: unknown; base?: string } = {}): Promise<T> {
  const headers: Record<string, string> = {}
  const tier = mockTier()
  if (tier) headers['X-Mock-Tier'] = tier
  const token = await accessToken()
  if (token) headers.Authorization = `Bearer ${token}`
  if (options.json !== undefined) headers['Content-Type'] = 'application/json'

  const response = await fetch(`${options.base ?? apiBase}${path}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.json === undefined ? undefined : JSON.stringify(options.json),
  })

  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as
      | { code?: string; title?: string; limitType?: string; requiredTier?: Tier }
      | null
    throw new ApiError(response.status, problem?.code ?? 'unknown', problem?.title ?? response.statusText, problem?.limitType, problem?.requiredTier)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/** Base URL of the billing API, which has its own mock. Prices are optional until it is running. */
export const billingBase: string =
  (import.meta.env.VITE_BILLING_BASE as string | undefined) ?? 'http://localhost:8090/api/billing/v1'

/** Where the marketplace lives. Locally this is Orderflow's web app. */
export const shopUrl: string = (import.meta.env.VITE_SHOP_URL as string | undefined) ?? 'http://localhost:5173'
