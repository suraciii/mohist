import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { stringInput } from "../../src/core/json.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import {
  contextWithOverrides,
  createSharedSessionFixture,
  resetAcpTestHooks,
  useAcpFakeTimers,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

async function runWithProviderDefaultModelWarning<T>(context: Parameters<typeof acpAgentAction>[0], operation: () => Promise<T>): Promise<T> {
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const result = await operation()

    expect(warningSpy).toHaveBeenCalledTimes(1)
    expect(warningSpy).toHaveBeenNthCalledWith(
      1,
      "mohist acp model not configured; using provider default",
      providerDefaultModelWarningContext(context),
    )
    return result
  } finally {
    warningSpy.mockRestore()
  }
}

function providerDefaultModelWarningContext(context: Parameters<typeof acpAgentAction>[0]) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: stringInput(context.with, "session") ?? context.workId,
    requestedModel: null,
    requestedModelSource: "none",
  }
}

describe("mohist/acp-agent resumed shared sessions", () => {
  it("ResumedSharedSessionStreamsThoughtChunks_ProbeWindowCrossed_DoesNotTimeoutOrAppendThoughtText", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { runtimeSessionId: "server-session-1" } })

    const context = contextWithOverrides({
      prompt: "long resumed task",
      session: "shared-session",
      livenessQuietThresholdMs: 50,
      probeTimeoutMs: 80,
      timeout: 1_000,
    }, undefined, shared.context())
    const action = runWithProviderDefaultModelWarning(context, () => acpAgentAction(context))
    await shared.agent.waitForPrompt()
    await vi.advanceTimersByTimeAsync(20)
    await vi.advanceTimersByTimeAsync(20)
    await vi.advanceTimersByTimeAsync(20)
    await vi.advanceTimersByTimeAsync(20)
    await vi.advanceTimersByTimeAsync(20)
    const result = await action

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

  it("PersistedSessionWithDifferentModelAndVariant_ResumesSamePhysicalSession", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", {
      sessionRecord: { acpSessionId: "server-session-1", model: "kimi-for-coding/k2p6" },
    })

    const action = acpAgentAction(contextWithOverrides({
      prompt: "continue with a different model",
      session: "shared-session",
      agent: { model: "openai/gpt-5.5", variant: "high" },
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context()))
    await shared.agent.waitForPrompt()
    for (let index = 0; index < 5; index += 1) await vi.advanceTimersByTimeAsync(20)
    const result = await action

    expect(result.status).toBe("success")
    expect(shared.agent.calls).toContainEqual(expect.objectContaining({ event: "resumeSession", sessionId: "server-session-1" }))
    expect(shared.agent.calls.some((entry) => entry.event === "newSession")).toBe(false)
    expect(shared.agent.calls).toContainEqual(expect.objectContaining({ event: "unstable_setSessionModel", sessionId: "server-session-1", modelId: "openai/gpt-5.5/high" }))
    expect(shared.agent.calls).toContainEqual(expect.objectContaining({ event: "prompt", sessionId: "server-session-1" }))
  })
})
