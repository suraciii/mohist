import type { ActionContext, JsonObject } from "../../src/core/types.js"
import type { ActionHost } from "../../src/actions/host.js"

export function hostFromContext(context: Pick<ActionContext, "workDir" | "signal" | "log">): ActionHost {
  return {
    workDir: context.workDir,
    signal: context.signal,
    log: context.log ?? null,
    exec: async () => ({ exitCode: 0, stdout: "", stderr: "" }),
  }
}

export function makeHost(overrides: Partial<ActionHost> = {}): ActionHost {
  return {
    workDir: "/tmp/test-workdir",
    signal: new AbortController().signal,
    log: null,
    exec: async () => ({ exitCode: 0, stdout: "", stderr: "" }),
    ...overrides,
  }
}
