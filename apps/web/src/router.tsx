import { createBrowserRouter } from 'react-router'
import { AppLayout } from '~/layout/app-layout'
import { AuthCallbackPage } from '~/pages/auth-callback-page'

// Pages load on demand so the first screen stays small.
export const router = createBrowserRouter([
  { path: '/auth/callback', element: <AuthCallbackPage /> },
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, lazy: async () => ({ Component: (await import('~/pages/pots-page')).PotsPage }) },
      { path: 'pots/:potId', lazy: async () => ({ Component: (await import('~/pages/pot-page')).PotPage }) },
      { path: 'plans', lazy: async () => ({ Component: (await import('~/pages/plans-page')).PlansPage }) },
      { path: 'guide', lazy: async () => ({ Component: (await import('~/pages/guide-page')).GuidePage }) },
      { path: 'claim', lazy: async () => ({ Component: (await import('~/pages/claim-page')).ClaimPage }) },
    ],
  },
])