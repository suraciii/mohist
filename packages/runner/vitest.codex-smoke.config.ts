import { configDefaults, defineConfig } from 'vitest/config'

// Opt-in real `codex app-server` compatibility smoke. Point
// MOHIST_CODEX_SMOKE_HOME at a dedicated managed Codex home (never the personal
// $HOME/.codex) with a supported Codex CLI. Without it the smoke reports a skip
// reason instead of running, which is why it stays out of the enforced
// integration track.
export default defineConfig({
  test: {
    include: ['tests/integration/codex-app-server-compatibility.test.ts'],
    exclude: configDefaults.exclude,
    unstubGlobals: true,
    unstubEnvs: true,
    clearMocks: true,
    setupFiles: ['./tests/setup.common.ts'],
  },
})
