import { afterEach, expect, it } from "vitest"
import { type ActionContext } from "../src/core/types.js"
import { getAcpProcessFactory, setAcpProcessFactoryForTest } from "../src/actions/acp/process.js"
import { assertExternalProcessAllowed } from "../src/system/process-policy.js"
import { fakeAcpProcess } from "./support/fake-acp.js"

afterEach(() => setAcpProcessFactoryForTest(null))

it("rejects a direct external process request in the default track", () => {
  expect(() => assertExternalProcessAllowed("test-process")).toThrow("external process forbidden in default test: test-process")
})

it("uses an injected ACP process factory without asking the process policy", () => {
  const fake = fakeAcpProcess()
  setAcpProcessFactoryForTest(() => fake)

  expect(getAcpProcessFactory()(context())).toBe(fake)
})

it("restores the default ACP factory under the default deny policy", () => {
  setAcpProcessFactoryForTest(null)

  expect(() => getAcpProcessFactory()(context())).toThrow("external process forbidden in default test")
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
    workDir: "/tmp/mohist-process-guard",
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}
