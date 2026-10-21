import type { ControlMode, Schemas } from '@greenhaus/ui'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api, billingBase, mockTier } from './http'

type S = Schemas

// The mock tier is part of every key, so switching it refetches everything.
export const key = (...parts: unknown[]) => ['greenhaus', mockTier(), ...parts]

export const useEntitlements = () => useQuery({ queryKey: key('entitlements'), queryFn: () => api<S['Entitlements']>('/me/entitlements') })
export const useTiers = () => useQuery({ queryKey: key('tiers'), queryFn: () => api<S['TierLimits'][]>('/tiers') })
export const useSpecies = () => useQuery({ queryKey: key('species'), queryFn: () => api<S['Species'][]>('/species'), staleTime: Infinity })

export const usePots = () => useQuery({ queryKey: key('pots'), queryFn: () => api<S['Pot'][]>('/pots') })
export const usePot = (id: string) => useQuery({ queryKey: key('pot', id), queryFn: () => api<S['Pot']>(`/pots/${id}`) })
export const useZones = (id: string) => useQuery({ queryKey: key('zones', id), queryFn: () => api<S['Zone'][]>(`/pots/${id}/zones`) })
export const usePlants = (id: string) => useQuery({ queryKey: key('plants', id), queryFn: () => api<S['Plant'][]>(`/pots/${id}/plants`) })
export const useRequirements = (id: string) => useQuery({ queryKey: key('requirements', id), queryFn: () => api<S['Requirements']>(`/pots/${id}/requirements`) })
export const useSuggestion = (id: string) =>
  useQuery({ queryKey: key('suggestion', id), queryFn: () => api<S['RequirementsSuggestion']>(`/pots/${id}/requirements/suggestion`) })
export const usePotStatus = (id: string) =>
  useQuery({ queryKey: key('status', id), queryFn: () => api<S['PotStatus']>(`/pots/${id}/status`), refetchInterval: 10_000 })
export const useActuators = (id: string) => useQuery({ queryKey: key('actuators', id), queryFn: () => api<S['Actuator'][]>(`/pots/${id}/actuators`) })
export const useAuditLog = (id: string) => useQuery({ queryKey: key('audit', id), queryFn: () => api<S['AuditEntry'][]>(`/pots/${id}/audit-log?limit=20`) })
export const useCareTasks = (id: string) => useQuery({ queryKey: key('care', id), queryFn: () => api<S['CareTask'][]>(`/pots/${id}/care-tasks`) })
export const useDevices = (id: string) => useQuery({ queryKey: key('devices', id), queryFn: () => api<S['LinkedDevice'][]>(`/pots/${id}/devices`) })

export function useClaimPreview(deviceId: string | null, token: string | null) {
  return useQuery({
    queryKey: key('claim-preview', deviceId, token),
    queryFn: () => api<S['ClaimPreview']>(`/claims/preview?deviceId=${encodeURIComponent(deviceId!)}&token=${encodeURIComponent(token!)}`),
    enabled: Boolean(deviceId && token),
    retry: false,
  })
}

type Subscription = { id: string; tier: S['Tier']; status: 'active' | 'past_due' | 'cancelled'; currentPeriodEnd: string; cancelAtPeriodEnd?: boolean }

/** The paid subscription, or null when there is none. Billing answers 404 for no subscription. */
export const useSubscription = () =>
  useQuery({
    queryKey: ['billing', 'subscription'],
    queryFn: async () => {
      try {
        return await api<Subscription>('/subscription', { base: billingBase })
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null
        throw error
      }
    },
    retry: false,
  })

/** Prices come from the billing service. If it is not running the plans page simply shows no prices. */
export const usePrices = () =>
  useQuery({
    queryKey: ['billing', 'plans'],
    queryFn: () => api<{ tier: S['Tier']; pricePerMonth: { amount: number; currency: string } }[]>('/plans', { base: billingBase }),
    retry: false,
  })

function useInvalidate() {
  const client = useQueryClient()
  return (...parts: unknown[]) => client.invalidateQueries({ queryKey: key(...parts) })
}

export function useCreatePot() {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (body: { name: string; kind: S['Pot']['kind'] }) => api<S['Pot']>('/pots', { method: 'POST', json: body }),
    onSuccess: () => Promise.all([invalidate('pots'), invalidate('entitlements')]),
  })
}

export function useSetViewMode(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (body: S['ViewModeUpdate']) => api<S['Pot']>(`/pots/${potId}/view-mode`, { method: 'PUT', json: body }),
    onSuccess: () => Promise.all([invalidate('pot', potId), invalidate('pots'), invalidate('zones', potId), invalidate('status', potId)]),
  })
}

export function useAddPlant(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (body: { speciesId: string; nickname: string }) => api<S['Plant']>(`/pots/${potId}/plants`, { method: 'POST', json: body }),
    onSuccess: () => Promise.all([invalidate('plants', potId), invalidate('suggestion', potId)]),
  })
}

export function useRemovePlant(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (plantId: string) => api<void>(`/pots/${potId}/plants/${plantId}`, { method: 'DELETE' }),
    onSuccess: () => Promise.all([invalidate('plants', potId), invalidate('suggestion', potId)]),
  })
}

export function useSetMode(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (v: { actuatorId: string; mode: ControlMode }) =>
      api<S['Actuator']>(`/pots/${potId}/actuators/${v.actuatorId}/mode`, { method: 'PUT', json: { mode: v.mode } }),
    onSuccess: () => Promise.all([invalidate('actuators', potId), invalidate('audit', potId)]),
  })
}

export function useRunActuator(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (v: { actuatorId: string; seconds: number }) =>
      api<S['CommandReceipt']>(`/pots/${potId}/actuators/${v.actuatorId}/run`, { method: 'POST', json: { durationSeconds: v.seconds } }),
    onSuccess: () => Promise.all([invalidate('actuators', potId), invalidate('audit', potId)]),
  })
}

export function useKillSwitch(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (enabled: boolean) => api<S['Pot']>(`/pots/${potId}/kill-switch`, { method: 'PUT', json: { enabled } }),
    onSuccess: () => Promise.all([invalidate('pot', potId), invalidate('pots'), invalidate('audit', potId)]),
  })
}

export function useCompleteTask(potId: string) {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (taskId: string) => api<S['CareTask']>(`/pots/${potId}/care-tasks/${taskId}/complete`, { method: 'POST' }),
    onSuccess: () => invalidate('care', potId),
  })
}

export function useClaim() {
  const invalidate = useInvalidate()
  return useMutation({
    mutationFn: (body: { deviceId: string; token: string; potId: string }) => api<S['Claim']>('/claims', { method: 'POST', json: body }),
    onSuccess: (_claim, vars) => Promise.all([invalidate('devices', vars.potId), invalidate('pots'), invalidate('status', vars.potId)]),
  })
}

function useBillingRefresh() {
  const client = useQueryClient()
  return () => Promise.all([client.invalidateQueries({ queryKey: ['billing'] }), client.invalidateQueries({ queryKey: key('entitlements') })])
}

export function useSubscribe() {
  const refresh = useBillingRefresh()
  return useMutation({
    mutationFn: (v: { tier: S['Tier']; paymentMethodToken: string }) =>
      api<Subscription>('/subscription', { method: 'POST', json: v, base: billingBase }),
    onSuccess: refresh,
  })
}

export function useCancelSubscription() {
  const refresh = useBillingRefresh()
  return useMutation({
    mutationFn: () => api<Subscription>('/subscription/cancel', { method: 'POST', base: billingBase }),
    onSuccess: refresh,
  })
}
