import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router'
import { getManager, signIn } from '~/auth/auth'

/** Where Keycloak sends the person back. The code in the address works once, so it is exchanged once. */
export function AuthCallbackPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const started = useRef(false)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    if (started.current) return
    started.current = true
    getManager()
      .signinRedirectCallback()
      .then((user) => {
        const target = (user.state as { returnTo?: string } | undefined)?.returnTo
        void navigate(target && target.startsWith('/') && target !== '/auth/callback' ? target : '/', { replace: true })
      })
      .catch(() => setFailed(true))
  }, [navigate])

  return (
    <main className="mx-auto flex min-h-svh max-w-md flex-col items-center justify-center gap-4 px-4 text-center">
      {failed ? (
        <>
          <p role="alert">{t('app.auth.failed')}</p>
          <button type="button" className="rounded-full bg-primary px-4 py-2 font-semibold text-primary-foreground" onClick={() => void signIn('/')}>
            {t('app.auth.signIn')}
          </button>
        </>
      ) : (
        <p role="status">{t('app.auth.signingIn')}</p>
      )}
    </main>
  )
}
