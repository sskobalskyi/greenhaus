import { defineConfig, devices } from '@playwright/test'

// Real sign-in against Keycloak and the real API. Start the stack first (see the README, "Sign-in end to end"):
//   docker compose -f docker-compose.yml -f docker-compose.auth.yml --profile real up -d --build api keycloak
//   docker compose up -d mock        (only for the prices on the plans page)
export default defineConfig({
  testDir: './e2e-auth',
  // Every test registers its own user, so they could run in parallel, but one Keycloak in dev mode is slow enough.
  workers: 1,
  fullyParallel: false,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['list'], ['html', { open: 'never' }]] : 'list',
  globalSetup: './e2e-auth/wait-for-stack.ts',
  use: {
    baseURL: 'http://localhost:5174',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm run dev',
    url: 'http://localhost:5174',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    env: {
      VITE_OIDC_AUTHORITY: 'http://localhost:8181/realms/greenhaus',
      VITE_MOCK: 'false',
      VITE_API_BASE: 'http://localhost:8091/api/greenhaus/v1',
    },
  },
})