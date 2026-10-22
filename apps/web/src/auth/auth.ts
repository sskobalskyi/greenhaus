import { i18n } from '@greenhaus/ui'
import { type User, UserManager } from 'oidc-client-ts'
import { useEffect, useState } from 'react'

const authority = import.meta.env.VITE_OIDC_AUTHORITY as string | undefined

/** Sign-in is on when an OpenID Connect authority is configured. Against the mock there is no login. */
export const authEnabled = Boolean(authority)

const manager = authority
  ? new UserManager({
      authority,
      client_id: (import.meta.env.VITE_OIDC_CLIENT_ID as string | undefined) ?? 'greenhaus-web',
      redirect_uri: `${window.location.origin}/auth/callback`,
      post_logout_redirect_uri: `${window.location.origin}/`,
      scope: 'openid profile email',
      automaticSilentRenew: true,
    })
  : null

export function getManager(): UserManager {
  if (!manager) throw new Error('Sign-in is not configured')
  return manager
}

/** The bearer token for the API, or null when signed out or sign-in is off. */
export async function accessToken(): Promise<string | null> {
  if (!manager) return null
  const user = await manager.getUser()
  return user && !user.expired ? user.access_token : null
}

/** Sends the person to Keycloak in their language, and brings them back to where they were. */
export function signIn(returnTo = window.location.pathname + window.location.search): Promise<void> {
  return getManager().signinRedirect({ state: { returnTo }, extraQueryParams: { ui_locales: i18n.language, kc_locale: i18n.language } })
}

let leaving = false

/** True once sign-out has started, so the app does not send the person straight back to sign in while Keycloak ends the session. */
export const isLeaving = () => leaving

export function signOut(): Promise<void> {
  leaving = true
  return getManager().signoutRedirect()
}

export type AuthState = { enabled: boolean; loading: boolean; user: User | null }

export function useAuth(): AuthState {
  const [state, setState] = useState<AuthState>({ enabled: authEnabled, loading: authEnabled, user: null })
  useEffect(() => {
    if (!manager) return
    let live = true
    const set = (user: User | null) => {
      if (live) setState({ enabled: true, loading: false, user: user && !user.expired ? user : null })
    }
    void manager.getUser().then(set)
    const loaded = (user: User) => set(user)
    const unloaded = () => set(null)
    manager.events.addUserLoaded(loaded)
    manager.events.addUserUnloaded(unloaded)
    manager.events.addAccessTokenExpired(unloaded)
    return () => {
      live = false
      manager.events.removeUserLoaded(loaded)
      manager.events.removeUserUnloaded(unloaded)
      manager.events.removeAccessTokenExpired(unloaded)
    }
  }, [])
  return state
}
