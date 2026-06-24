import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import type { ActionContext } from "../../src/core/types.js"
import { ServerConnection } from "../../src/server/connection.js"
import {
  baseContext,
  contextWithOverrides,
  createFakeProcess,
  createSharedSessionFixture,
  createTrackedFakeProcess,
  FakeAcpAgent,
  FakeServerConnection,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  delete process.env.MOHIST_OPENCODE_LOG_DIR
})

describe("mohist/acp-agent cancelAndReturn bounded cleanup", () => {
  it("EphemeralSessionCancelHangs_CleanupForcesProcessKill_AndReturnsWithinBound", async () => {
    const agent = new FakeAcpAgent("cancel-hangs")
    const tracked = createTrackedFakeProcess(agent, { hangCancelWrites: true })
    setAcpProcessFactoryForTest(() => tracked)
    const serverConnection = new FakeServerConnection()

    const startedAt = Date.now()
    const result = await acpAgentAction({
      ...baseContext({ prompt: "hanging task", timeout: 100 }),
      serverConnection: serverConnection as unknown as ServerConnection,
    })
    const elapsed = Date.now() - startedAt

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Timed out")
    expect(tracked.cleanupCount()).toBeGreaterThanOrEqual(1)
    expect(elapsed).toBeGreaterThanOrEqual(4_500)
    expect(elapsed).toBeLessThan(10_000)
  }, 15_000)

  it("EphemeralSessionCancelResolvesPromptly_NoForceCleanupFromTimeoutRace", async () => {
    const agent = new FakeAcpAgent("cancel-hangs")
    agent.cancelHangs = false
    const tracked = createTrackedFakeProcess(agent)
    setAcpProcessFactoryForTest(() => tracked)
    const serverConnection = new FakeServerConnection()
    const controller = new AbortController()
    setTimeout(() => controller.abort(), 30)

    const cleanupBefore = tracked.cleanupCount()
    const startedAt = Date.now()
    const result = await acpAgentAction({
      ...baseContext({ prompt: "abort task", timeout: 5_000 }, controller.signal),
      serverConnection: serverConnection as unknown as ServerConnection,
    })
    const elapsed = Date.now() - startedAt

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
    expect(elapsed).toBeLessThan(2_000)
    const extraCleanups = tracked.cleanupCount() - cleanupBefore
    expect(extraCleanups).toBeLessThanOrEqual(1)
  })

  it("SharedSessionCancelHangs_NoProcessIsKilled", async () => {
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "server-session-1" } })
    shared.agent.cancelHangs = true

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "long shared task",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 100,
    }, undefined, shared.context()))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Timed out")
    expect(shared.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
  })
})

describe("mohist/acp-agent monitorPrompt prompt_timeout diagnostics", () => {
  it("PromptTimesOutWithProviderErrorInLog_ErrorMessageContainsDiagnostic_AndFailureCategoryIsPromptTimeout", async () => {
    const logDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = logDir
    try {
      await writeFile(join(logDir, "2026-06-03T164901.log"), [
        'ERROR 2026-06-03T16:49:06 service=llm providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=fake-session-1 small=false agent=build mode=primary error={"error":{"name":"AI_APICallError","statusCode":2056,"responseBody":"{\\"type\\":\\"error\\",\\"error\\":{\\"type\\":\\"token_plan_limit_error\\",\\"message\\":\\"Token Plan usage limit reached\\"}}","isRetryable":true}} stream error',
        "",
      ].join("\n"))
      const fixture = createFixture("cancel-hangs")
      setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

      const result = await acpAgentAction(fixture.context({
        prompt: "hanging prompt",
        timeout: 100,
        livenessQuietThresholdMs: 5_000,
        probeTimeoutMs: 5_000,
      }))

      expect(result.status).toBe("failure")
      expect(result.message ?? "").toContain("Timed out after")
      expect(result.message ?? "").toContain("Opencode provider error: 2056 token_plan_limit_error on minimax-coding-plan/MiniMax-M3 - Token Plan usage limit reached")

      const output = JSON.parse(result.output ?? "{}") as Record<string, unknown>
      const providerError = output.providerError as Record<string, unknown> | undefined
      expect(providerError?.statusCode).toBe(2056)
      expect(providerError?.errorType).toBe("token_plan_limit_error")
      expect(providerError?.message).toContain("Token Plan usage limit reached")

      const failed = fixture.serverConnection.calls
        .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
        .map((entry) => entry.payload as Record<string, unknown>)
        .find((payload) => payload.status === "failed")
      expect(failed).toBeTruthy()
      expect(failed?.failureReason).toBe("prompt_timeout")
      expect((failed?.providerError as Record<string, unknown>)?.statusCode).toBe(2056)

      const terminal = fixture.serverConnection.calls
        .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
        .map((entry) => entry.payload as Record<string, unknown>)
        .at(-1)
      expect(terminal?.failureCategory).toBe("prompt_timeout")
      expect(String(terminal?.failureReason)).toContain("Opencode provider error: 2056 token_plan_limit_error")
    } finally {
      await rm(logDir, { recursive: true, force: true })
    }
  })

  it("PromptTimesOutWithEmptyLogDir_ErrorContainsNoDiagnostic_AndFailureCategoryStillPromptTimeout", async () => {
    const logDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-empty-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = logDir
    try {
      const fixture = createFixture("cancel-hangs")
      setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

      const result = await acpAgentAction(fixture.context({
        prompt: "hanging prompt no log",
        timeout: 100,
        livenessQuietThresholdMs: 5_000,
        probeTimeoutMs: 5_000,
      }))

      expect(result.status).toBe("failure")
      expect(result.message ?? "").toContain("Timed out after")
      expect(result.message ?? "").not.toContain("Opencode provider error")

      const output = JSON.parse(result.output ?? "{}") as Record<string, unknown>
      expect(output.providerError).toBeUndefined()

      const failed = fixture.serverConnection.calls
        .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
        .map((entry) => entry.payload as Record<string, unknown>)
        .find((payload) => payload.status === "failed")
      expect(failed).toBeTruthy()
      expect(failed?.failureReason).toBe("prompt_timeout")
      expect(failed?.providerError).toBeUndefined()

      const terminal = fixture.serverConnection.calls
        .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
        .map((entry) => entry.payload as Record<string, unknown>)
        .at(-1)
      expect(terminal?.failureCategory).toBe("prompt_timeout")
    } finally {
      await rm(logDir, { recursive: true, force: true })
    }
  })

  it("PromptTimesOut_EmitsLivenessFailedEventWithPromptTimeoutFailureReason", async () => {
    const fixture = createFixture("cancel-hangs")
    setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

    await acpAgentAction(fixture.context({
      prompt: "hanging prompt liveness event",
      timeout: 100,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
    }))

    const livenessEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)
    const failed = livenessEvents.find((payload) => payload.status === "failed")
    expect(failed).toBeTruthy()
    expect(failed?.failureReason).toBe("prompt_timeout")
    expect(failed?.acpSessionId).toBe("fake-session-1")
  })
})

function createFixture(scenario: "cancel-hangs") {
  const timeline: Array<{ event: string }> = []
  const agent = new FakeAcpAgent(scenario, timeline)
  const serverConnection = new FakeServerConnection(timeline)
  setAcpProcessFactoryForTest(() => createFakeProcess(agent))
  return {
    agent,
    serverConnection,
    timeline,
    context(withInput: Record<string, unknown>, signal = new AbortController().signal, overrides: Partial<ActionContext> = {}): ActionContext {
      return {
        ...baseContext(withInput, signal),
        serverConnection: serverConnection as unknown as ServerConnection,
        ...overrides,
      }
    },
  }
}