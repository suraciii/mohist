import { afterEach, describe, expect, it } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import {
  contextWithOverrides,
  createFixture,
  createSharedFixture,
  createSharedSessionFixture,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  delete process.env.MOHIST_OPENCODE_LOG_DIR
})

describe("mohist/acp-agent existing shared session reuse", () => {
  it("SharedAcpThoughtAndToolUpdatesArrive_LivenessMonitored_DoNotProbeWhileAgentIsActive", async () => {
    const fixture = createSharedFixture("liveness-non-message")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", session: "build", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    expect(fixture.server.events.map((entry) => entry.type)).toEqual(
      expect.arrayContaining(["reasoning.delta", "tool_call.started", "tool_call.completed", "session.closed"]),
    )
  })

  it("NamedWorkflowSessionStartsNewAcpSession_ReportsPhysicalSessionIdToServerWithoutRenaming", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
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
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "shared-session-1" } })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "long shared task",
      session: "shared-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 80,
      timeout: 1_000,
    }, undefined, shared.context()))

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
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "shared-session-1", model: "openai/gpt-5.5" } })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "reuse shared session",
      session: "shared-session",
      agent: { model: "openai/gpt-5.5" },
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context()))

    expect(result.status).toBe("success")
    const setModelIndex = shared.agent.calls.findIndex((entry) => entry.event === "setSessionConfigOption" && entry.configId === "model" && entry.value === "openai/gpt-5.5")
    const promptIndex = shared.agent.calls.findIndex((entry) => entry.event === "prompt")
    expect(setModelIndex).toBeGreaterThanOrEqual(0)
    expect(setModelIndex).toBeLessThan(promptIndex)
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.agent.calls.some((entry) => entry.event === "newSession")).toBe(false)
  })

  it("ExistingSharedSessionWithDifferentRequestedModel_StartsNewSessionInsteadOfResumingOldModel", async () => {
    const shared = createSharedSessionFixture("thought-liveness", {
      cachedModel: "kimi-for-coding/k2p6",
      newSessionId: "replacement-session-1",
      sessionRecord: { acpSessionId: "shared-session-1", model: "kimi-for-coding/k2p6" },
    })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "switch shared session model",
      session: "shared-session",
      agent: { model: "openai/gpt-5.5" },
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context()))

    expect(result.status).toBe("success")
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.agent.calls.some((entry) => entry.event === "newSession")).toBe(true)
    expect(shared.serverConnection.calls).toContainEqual(expect.objectContaining({
      event: "attachWorkflowAgentSession",
      sessionName: "shared-session",
      body: expect.objectContaining({ agentSessionId: "replacement-session-1", model: "openai/gpt-5.5" }),
    }))
    const setModelIndex = shared.agent.calls.findIndex((entry) => entry.event === "setSessionConfigOption" && entry.sessionId === "replacement-session-1" && entry.value === "openai/gpt-5.5")
    const promptIndex = shared.agent.calls.findIndex((entry) => entry.event === "prompt" && entry.sessionId === "replacement-session-1")
    expect(setModelIndex).toBeGreaterThanOrEqual(0)
    expect(setModelIndex).toBeLessThan(promptIndex)
  })
})
