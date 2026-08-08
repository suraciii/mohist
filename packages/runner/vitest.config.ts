import { configDefaults, defineConfig } from 'vitest/config'

// Runner test configuration. Globals auto-restore reduces the flake risk of a
// stubbed globalThis.fetch leaking across tests; clearMocks resets mock call
// state between tests so ordering cannot bias assertions. restoreMocks is
// intentionally NOT enabled — runner specs rely on vi.spyOn spies that must
// persist across the file, and restoring them would break those setups.
export default defineConfig({
  test: {
    unstubGlobals: true,
    unstubEnvs: true,
    clearMocks: true,
    exclude: [...configDefaults.exclude, 'tests/integration/**'],
    setupFiles: ['./tests/setup.common.ts'],
  },
})
