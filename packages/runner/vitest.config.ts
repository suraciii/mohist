import { defineConfig } from "vitest/config"

export default defineConfig({
  test: {
    // Most runner tests are fast (<200ms) but several execute real
    // git operations (clone/fetch/checkout) which can take several
    // seconds each, especially when 30+ test files run in parallel.
    // 30s gives a comfortable headroom for the git-heavy specs
    // without changing the default for the fast unit tests.
    testTimeout: 30_000,
    hookTimeout: 10_000,
    pool: "forks",
  },
})
