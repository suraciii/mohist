import { afterEach, describe, expect, it } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import {
  contextWithOverrides,
  createSharedSessionFixture,
  resetAcpTestHooks,
  runAcpActionUntilSettled,
  useAcpFakeTimers,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/acp-agent resumed shared sessions", () => {
  it("ResumedSharedSessionStreamsThoughtChunks_ProbeWindowCrossed_DoesNotTimeoutOrAppendThoughtText", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "server-session-1" } })

    const result = await runAcpActionUntilSettled(acpAgentAction(contextWithOverrides({
      prompt: "long resumed task",
      session: "shared-session",
      livenessQuietThresholdMs: 50,
      probeTimeoutMs: 80,
      timeout: 1_000,
    }, undefined, shared.context())))

    expect(result.status).toBe("success")
    expect(shared.serverConnection.calls.some((entry) => entry.event === "getWorkflowAgentSession" || entry.event === "openWorkflowAgentSession")).toBe(true)
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession" && entry.sessionId === "server-session-1")).toBe(true)
    expect(shared.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    expect(shared.agent.calls.some((entry) => entry.event === "thought")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "reasoning.delta")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness" && (entry.payload as { status?: string }).status === "failed")).toBe(false)
    expect(result.message ?? "").not.toContain("Session liveness probe timed out")
    expect(JSON.parse(result.output ?? "{}").text).toBe("")
  })
})
