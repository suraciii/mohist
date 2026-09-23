import { configDefaults, defineConfig } from 'vitest/config'

// The real `codex app-server` compatibility smoke is opt-in through
// vitest.codex-smoke.config.ts. The enforced integration track fails on any
// skipped case, and the smoke must skip when no dedicated managed Codex home is
// configured, so it is excluded from the default population here.
export default defineConfig({
  test: {
    include: ['tests/integration/**/*.spec.ts', 'tests/integration/**/*.test.ts'],
    exclude: [...configDefaults.exclude, 'tests/integration/codex-app-server-compatibility.test.ts'],
    unstubGlobals: true,
    unstubEnvs: true,
    clearMocks: true,
    setupFiles: ['./tests/setup.common.ts'],
  },
})
