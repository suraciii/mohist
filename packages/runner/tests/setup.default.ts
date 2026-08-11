import { afterEach, beforeEach } from "vitest"
import { setExternalProcessPolicyForTest, type ExternalProcessPolicy } from "../src/system/process-policy.js"
import { setPiRuntimeFactoryForTest } from "../src/runtime/pi/index.js"

const denyExternalProcess: ExternalProcessPolicy = {
  assertAllowed(label) {
    throw new Error(`external process forbidden in default test: ${label}`)
  },
  register() {},
}

setExternalProcessPolicyForTest(denyExternalProcess)
setPiRuntimeFactoryForTest(() => ({
  start: async () => ({ ok: true, value: { ready: true, diagnostic: null, catalog: { models: [] } }, diagnostics: [] }),
  ready: () => true,
  diagnostic: () => null,
  catalog: () => ({ models: [] }),
  createSession: async () => ({ ok: true, value: { runtimeSessionId: "/test/pi-session", workDir: "/test" }, diagnostics: [] }),
  runTurn: async () => ({ ok: true, value: { facts: { finalAssistantText: null, runtimeSessionId: "/test/pi-session", workDir: "/test" }, diagnostics: [] }, diagnostics: [] }),
  shutdown: async () => {},
} as never))
beforeEach(() => {
  setExternalProcessPolicyForTest(denyExternalProcess)
  setPiRuntimeFactoryForTest(() => ({
    start: async () => ({ ok: true, value: { ready: true, diagnostic: null, catalog: { models: [] } }, diagnostics: [] }),
    ready: () => true,
    diagnostic: () => null,
    catalog: () => ({ models: [] }),
    createSession: async () => ({ ok: true, value: { runtimeSessionId: "/test/pi-session", workDir: "/test" }, diagnostics: [] }),
    runTurn: async () => ({ ok: true, value: { facts: { finalAssistantText: null, runtimeSessionId: "/test/pi-session", workDir: "/test" }, diagnostics: [] }, diagnostics: [] }),
    shutdown: async () => {},
  } as never))
})
afterEach(() => {
  setExternalProcessPolicyForTest(denyExternalProcess)
  setPiRuntimeFactoryForTest(null)
})
