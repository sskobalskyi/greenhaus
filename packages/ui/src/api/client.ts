import type { components } from './schema'

export type Schemas = components['schemas']
export type Metric = Schemas['Metric']
export type Tier = Schemas['Tier']
export type ControlMode = Schemas['ControlMode']
export type ReadingState = Schemas['ReadingState']

/**
 * Base URL of the Greenhaus API. Locally this is the mock server (`docker compose up mock`),
 * which implements the same contract as the real service.
 */
export const apiBase: string =
  (import.meta.env.VITE_API_BASE as string | undefined) ?? 'http://localhost:8090/api/greenhaus/v1'

export async function getJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, init)
  if (!response.ok) throw new Error(`${response.status} ${response.statusText} for ${path}`)
  return (await response.json()) as T
}
