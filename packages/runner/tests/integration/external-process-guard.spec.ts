import { beforeAll, expect, it, vi } from "vitest"
import { type ActionContext } from "../../src/core/types.js"
import { createSpawnedAcpProcess } from "../../src/actions/acp/process.js"
import { assertExternalProcessAllowed } from "../../src/system/process-policy.js"

let beforeAllPolicyAllowed = false

beforeAll(() => {
  assertExternalProcessAllowed("integration-before-all")
  beforeAllPolicyAllowed = true
})

it("installs the integration policy before beforeEach hooks", () => {
  expect(beforeAllPolicyAllowed).toBe(true)
})

it("allows the real ACP factory in the integration track", async () => {
  vi.stubEnv("MOHIST_AGENT_COMMAND", process.execPath)
  vi.stubEnv("MOHIST_AGENT_ARGS", JSON.stringify(["-e", "setInterval(() => {}, 1000)"]))

  const acpProcess = createSpawnedAcpProcess(context())

  expect(acpProcess.processPid).not.toBeNull()
  acpProcess.markInitialized()
  void acpProcess.exitFailure.catch(() => {})
  await acpProcess.cleanup()
})

function context(): ActionContext {
  return {
    workflowRunId: "workflow-guard",
    workId: "guard.1",
    workType: "task",
    stage: "build",
    title: "External process guard",
    uses: "mohist/acp-agent",
    with: {} as never,
    variables: {} as never,
    workDir: process.cwd(),
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}
