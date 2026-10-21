import { defineConfig, devices } from '@playwright/test'

// The app talks to the mock backend on port 8090: `docker compose up -d mock` from the repository root.
export default defineConfig({
  testDir: './e2e',
  // The mock keeps state in memory and the tests reset it, so they run one after another.
  workers: 1,
  fullyParallel: false,
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: {
    baseURL: 'http://localhost:5174',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm run build && npm run preview',
    url: 'http://localhost:5174',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
})
