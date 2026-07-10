import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { stringInput } from "../../src/core/json.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import {
  contextWithOverrides,
  createFixture,
  createSharedFixture,
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

function runDefaultModelAction(context: Parameters<typeof acpAgentAction>[0]) {
  return runWithProviderDefaultModelWarning(context, () => acpAgentAction(context))
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

describe("mohist/acp-agent existing shared session reuse", () => {
  it("SharedAcpThoughtAndToolUpdatesArrive_LivenessMonitored_DoNotProbeWhileAgentIsActive", async () => {
    useAcpFakeTimers()
    const fixture = createSharedFixture("liveness-non-message")

    const result = await runAcpActionUntilSettled(runDefaultModelAction(fixture.context({ prompt: "long task", session: "build", livenessQuietThresholdMs: 100, probeTimeoutMs: 500, timeout: 2_000 })))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    expect(fixture.server.events.map((entry) => entry.type)).toEqual(
      expect.arrayContaining(["reasoning.delta", "tool_call.started", "tool_call.completed", "session.closed"]),
    )
  })

  it("NamedWorkflowSessionStartsNewAcpSession_ReportsPhysicalSessionIdToServerWithoutRenaming", async () => {
    const fixture = createFixture("basic")

    const result = await runDefaultModelAction(fixture.context({
      prompt: "review retry",
      session: "check",
    }, undefined, {
      workId: "ai-review.2",
      workType: "task",
      stage: "check",
    }))

    expect(result.status).toBe("success")
    const sessionCalls = fixture.serverConnection.calls
      .filter((entry) => entry.event === "ensureWorkflowAgentSession" || entry.event === "attachWorkflowAgentSession" || entry.event === "workflowAgentSessionEvents")
    expect(sessionCalls.map((entry) => entry.sessionName)).toEqual(expect.arrayContaining(["check"]))
    expect(sessionCalls.some((entry) => entry.sessionName !== "check")).toBe(false)
    expect(fixture.serverConnection.calls).toContainEqual(expect.objectContaining({
      event: "attachWorkflowAgentSession",
      sessionName: "check",
      body: expect.objectContaining({ agentSessionId: "fake-session-1" }),
    }))
  })

  it("ExistingSharedSessionStreamsThoughtChunks_ProbeWindowCrossed_DoesNotTimeoutOrAppendThoughtText", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "shared-session-1" } })

    const result = await runAcpActionUntilSettled(runDefaultModelAction(contextWithOverrides({
      prompt: "long shared task",
      session: "shared-session",
      livenessQuietThresholdMs: 50,
      probeTimeoutMs: 80,
      timeout: 1_000,
    }, undefined, shared.context())))

    expect(result.status).toBe("success")
    expect(shared.agent.calls.filter((entry) => entry.event === "prompt").length).toBe(1)
    expect(shared.agent.calls.some((entry) => entry.event === "thought")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "getWorkflowAgentSession" || entry.event === "openWorkflowAgentSession")).toBe(true)
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "reasoning.delta")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness" && (entry.payload as { status?: string }).status === "failed")).toBe(false)
    expect(result.message ?? "").not.toContain("Session liveness probe timed out")
    expect(JSON.parse(result.output ?? "{}").text).toBe("")
  })

  it("ExistingSharedSessionWithRequestedModel_SetsModelBeforePromptWithoutResume", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "shared-session-1", model: "openai/gpt-5.5" } })

    const result = await runAcpActionUntilSettled(acpAgentAction(contextWithOverrides({
      prompt: "reuse shared session",
      session: "shared-session",
      agent: { model: "openai/gpt-5.5" },
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context())))

    expect(result.status).toBe("success")
    const setModelIndex = shared.agent.calls.findIndex((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "openai/gpt-5.5")
    const promptIndex = shared.agent.calls.findIndex((entry) => entry.event === "prompt")
    expect(setModelIndex).toBeGreaterThanOrEqual(0)
    expect(setModelIndex).toBeLessThan(promptIndex)
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.agent.calls.some((entry) => entry.event === "newSession")).toBe(false)
  })

  it("ExistingSharedSessionWithDifferentRequestedModel_StartsNewSessionInsteadOfResumingOldModel", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", {
      cachedModel: "kimi-for-coding/k2p6",
      newSessionId: "replacement-session-1",
      sessionRecord: { acpSessionId: "shared-session-1", model: "kimi-for-coding/k2p6" },
    })

    const result = await runAcpActionUntilSettled(acpAgentAction(contextWithOverrides({
      prompt: "switch shared session model",
      session: "shared-session",
      agent: { model: "openai/gpt-5.5" },
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context())))

    expect(result.status).toBe("success")
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.agent.calls.some((entry) => entry.event === "newSession")).toBe(true)
    expect(shared.serverConnection.calls).toContainEqual(expect.objectContaining({
      event: "attachWorkflowAgentSession",
      sessionName: "shared-session",
      body: expect.objectContaining({ agentSessionId: "replacement-session-1", model: "openai/gpt-5.5" }),
    }))
    const setModelIndex = shared.agent.calls.findIndex((entry) => entry.event === "unstable_setSessionModel" && entry.sessionId === "replacement-session-1" && entry.modelId === "openai/gpt-5.5")
    const promptIndex = shared.agent.calls.findIndex((entry) => entry.event === "prompt" && entry.sessionId === "replacement-session-1")
    expect(setModelIndex).toBeGreaterThanOrEqual(0)
    expect(setModelIndex).toBeLessThan(promptIndex)
  })

  it("ExistingSharedSessionSameModelDifferentVariant_StartsFreshSessionDeliversNewVariant", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", {
      cachedModel: "anthropic/claude-sonnet-4-5/max",
      newSessionId: "variant-flip-session-1",
      sessionRecord: { acpSessionId: "shared-session-1", model: "anthropic/claude-sonnet-4-5/max" },
    })

    const result = await runAcpActionUntilSettled(acpAgentAction(contextWithOverrides({
      prompt: "switch shared session variant",
      session: "shared-session",
      agent: { model: "anthropic/claude-sonnet-4-5", variant: "high" },
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context())))

    expect(result.status).toBe("success")
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.agent.calls.some((entry) => entry.event === "newSession")).toBe(true)
    expect(shared.agent.calls.some((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude-sonnet-4-5/high")).toBe(true)
  })
})
