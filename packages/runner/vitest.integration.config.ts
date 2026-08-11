import { configDefaults, defineConfig } from "vitest/config"

export default defineConfig({
  test: {
    include: ["tests/integration/**/*.spec.ts"],
    exclude: configDefaults.exclude,
    unstubGlobals: true,
    unstubEnvs: true,
    clearMocks: true,
    setupFiles: ["./tests/setup.common.ts"],
  },
})
