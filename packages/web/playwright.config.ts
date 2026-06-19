import { defineConfig, devices } from '@playwright/test'

/**
 * Non-a11y Playwright config for behavioral e2e tests (e.g. the settings
 * search dialog). Modeled on `playwright.a11y.config.ts` (which covers
 * axe-core scans) but pointed at `./tests/e2e` instead of `./tests/a11y`.
 *
 * The two configs stay independent: a11y scans are slow because axe-core
 * inspects every node, while behavioral e2e tests just need a clean
 * preview server and the chromium project. Sharing one config would force
 * every run to pay the axe-core cost and would mix concerns in a single
 * reporter output.
 */
export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:4173',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'npm run build && npm run preview -- --host 127.0.0.1 --port 4173',
    url: 'http://127.0.0.1:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        launchOptions: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
          ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE }
          : undefined,
      },
    },
  ],
})
