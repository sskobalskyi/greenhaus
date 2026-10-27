import { Badge, LanguageSwitcher, NotificationBell, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, ThemeToggle, type Tier } from '@greenhaus/ui'
import { Leaf, LogOut } from 'lucide-react'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { isLeaving, signIn, signOut, useAuth } from '~/auth/auth'
import { useEntitlements, useMarkAllNotificationsRead, useMarkNotificationRead, useNotifications } from '~/api/hooks'
import { mockTier, setMockTier } from '~/api/http'

const link = ({ isActive }: { isActive: boolean }) =>
  `rounded-full px-3 py-1.5 text-sm font-semibold whitespace-nowrap transition-colors ${isActive ? 'bg-primary text-primary-foreground' : 'text-foreground hover:bg-secondary'}`

/** With sign-in on, nothing of the app (and none of its requests) appears until there is a signed-in person. The set-up guide and the plans stay open, so limits and prices can be read before signing up. */
export function AppLayout() {
  const { t } = useTranslation()
  const auth = useAuth()
  const { pathname } = useLocation()
  const blocked = auth.enabled && !auth.user && !pathname.startsWith('/guide') && !pathname.startsWith('/plans')

  useEffect(() => {
    if (blocked && !auth.loading && !isLeaving()) void signIn()
  }, [blocked, auth.loading])

  if (blocked) {
    return (
      <main className="mx-auto flex min-h-svh max-w-md items-center justify-center px-4 text-center">
        <p role="status">{t('app.auth.signingIn')}</p>
      </main>
    )
  }
  return <Shell />
}

function Shell() {
  const { t } = useTranslation()
  const auth = useAuth()
  const entitlements = useEntitlements()
  const notifications = useNotifications()
  const markRead = useMarkNotificationRead()
  const markAllRead = useMarkAllNotificationsRead()
  const navigate = useNavigate()
  const current = mockTier()

  return (
    <div className="min-h-svh bg-background text-foreground">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:rounded focus:bg-primary focus:px-3 focus:py-2 focus:text-primary-foreground">
        {t('app.skipToContent')}
      </a>
      <header className="sticky top-0 z-10 border-b bg-background/90 backdrop-blur">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2 sm:gap-x-4 sm:py-3">
          <NavLink to="/" className="flex shrink-0 items-center gap-2 font-display text-xl font-semibold" aria-label={t('app.name')}>
            <span className="flex size-8 items-center justify-center rounded-xl bg-primary text-primary-foreground">
              <Leaf className="size-5" aria-hidden />
            </span>
            {/* On a narrow phone the mark alone leaves room for the buttons next to it. */}
            <span className="max-[420px]:sr-only">{t('app.name')}</span>
          </NavLink>
          <nav className="order-last flex w-full items-center gap-1 overflow-x-auto pb-1 sm:order-none sm:w-auto sm:flex-1 sm:pb-0" aria-label={t('app.nav.label')}>
            <NavLink to="/" end className={link}>
              {t('app.nav.pots')}
            </NavLink>
            <NavLink to="/plans" className={link}>
              {t('app.nav.plans')}
            </NavLink>
            <NavLink to="/guide" className={link}>
              {t('app.nav.guide')}
            </NavLink>
          </nav>
          <div className="ml-auto flex flex-wrap items-center justify-end gap-1.5 sm:gap-2">
            {entitlements.data && <Badge variant="secondary" className="hidden sm:inline-flex">{t(`tier.name.${entitlements.data.tier}`)}</Badge>}
            {import.meta.env.VITE_MOCK !== 'false' && (
              <span className="flex items-center gap-1.5">
                <span className="text-xs font-semibold text-muted-foreground" aria-hidden>
                  {t('app.mockTierLabel')}
                </span>
              <Select
                value={current ?? 'default'}
                onValueChange={(value) => {
                  setMockTier(value === 'default' ? null : (value as Tier))
                  window.location.reload()
                }}
              >
                <SelectTrigger size="sm" aria-label={t('app.mockTier')} className="w-32">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="default">{t('app.mockTier')}</SelectItem>
                  {(['novice', 'gardener', 'druid'] as const).map((tier) => (
                    <SelectItem key={tier} value={tier}>
                      {t(`tier.name.${tier}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              </span>
            )}
            {auth.enabled &&
              (auth.user ? (
                <span className="flex items-center gap-2">
                  {/* On a phone there is no room for the name next to the buttons, so it stays for screen readers and the title. */}
                  <span className="sr-only text-sm font-semibold sm:not-sr-only" data-testid="signed-in-as">
                    {auth.user.profile.name ?? auth.user.profile.preferred_username}
                  </span>
                  <button
                    type="button"
                    className="flex items-center gap-1.5 rounded-full px-2 py-1.5 text-sm font-semibold whitespace-nowrap hover:bg-secondary sm:px-3"
                    aria-label={t('app.auth.signOut')}
                    title={auth.user.profile.name ?? auth.user.profile.preferred_username}
                    onClick={() => void signOut()}
                  >
                    <LogOut className="size-4 sm:hidden" aria-hidden />
                    <span className="hidden sm:inline">{t('app.auth.signOut')}</span>
                  </button>
                </span>
              ) : (
                <button type="button" className="rounded-full bg-primary px-3 py-1.5 text-sm font-semibold text-primary-foreground" onClick={() => void signIn()}>
                  {t('app.auth.signIn')}
                </button>
              ))}
            {(!auth.enabled || auth.user) && (
              <NotificationBell
                notifications={notifications.data ?? []}
                onReadAll={() => markAllRead.mutate()}
                onOpen={(n) => {
                  if (!n.readAt) markRead.mutate(n.id)
                  if (n.potId) void navigate(`/pots/${n.potId}`)
                }}
              />
            )}
            <LanguageSwitcher />
            <ThemeToggle />
          </div>
        </div>
      </header>
      <main id="main" className="mx-auto max-w-6xl px-4 py-6">
        <Outlet />
      </main>
    </div>
  )
}
