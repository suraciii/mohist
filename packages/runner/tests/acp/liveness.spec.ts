import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import {
  createSessionLivenessState,
  monitorPrompt,
} from "../../src/actions/acp/liveness.js"
import type { ActionContext } from "../../src/core/types.js"
import type { OpencodeProviderErrorDiagnostic } from "../../src/runtime/opencode-log-diagnostics.js"
import { ServerConnection } from "../../src/server/connection.js"
import { deferred } from "../support/deferred.js"
import {
  baseContext,
  contextWithOverrides,
  createFakeProcess,
  createSharedSessionFixture,
  createTrackedFakeProcess,
  FakeAcpAgent,
  FakeServerConnection,
  resetAcpTestHooks,
  useAcpProviderDiagnostic,
  useAcpFakeTimers,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/acp-agent monitorPrompt provider fail-fast", () => {
  it("LongPollingPrompt_ReusesAbortSignalWatcher", async () => {
    useAcpFakeTimers()
    const controller = new AbortController()
    const addSpy = vi.spyOn(controller.signal, "addEventListener")
    const removeSpy = vi.spyOn(controller.signal, "removeEventListener")
    const context = baseContext({ prompt: "hanging task" }, controller.signal)
    const connection = {
      async prompt() {
        promptStarted.resolve()
        return await new Promise(() => {})
      },
      async cancel() {
        return {}
      },
    }

    const promptStarted = deferred<void>()
    const action = monitorPrompt(context, connection as never, "fake-session-1", "do the work", {
      timeoutMs: 50,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      livenessState: createSessionLivenessState(),
      waitForData: () => new Promise<"data">(() => {}),
      providerErrorCheckIntervalMs: 1,
    })
    await promptStarted.promise
    await vi.advanceTimersByTimeAsync(50)
    const result = await action

    expect(result).not.toBe("completed")
    expect(addSpy).toHaveBeenCalledTimes(1)
    expect(removeSpy).toHaveBeenCalledTimes(1)
  })

  it("TokenPlanProviderDiagnostic_MonitorFailsPromptImmediately", async () => {
    useAcpFakeTimers()
    useAcpProviderDiagnostic(tokenPlanDiagnostic(new Date().toISOString()))
    const context = {
      ...baseContext({ prompt: "hanging task" }),
      serverConnection: new FakeServerConnection() as unknown as ServerConnection,
    }
    let cancelled = false
    const promptStarted = deferred<void>()
    const connection = {
      async prompt() {
        promptStarted.resolve()
        return await new Promise(() => {})
      },
      async cancel() {
        cancelled = true
        return {}
      },
    }

    const action = monitorPrompt(context, connection as never, "fake-session-1", "do the work", {
      timeoutMs: 1_000,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      livenessState: createSessionLivenessState(),
      waitForData: () => new Promise<"data">(() => {}),
      providerErrorCheckIntervalMs: 1,
    })
    await promptStarted.promise
    await vi.advanceTimersByTimeAsync(1)
    const result = await action

    expect(result).not.toBe("completed")
    if (result === "completed") return
    expect(result.failureReason).toBe("provider_error")
    expect(result.error).toContain("Opencode provider error: AI_APICallError on minimax-coding-plan/MiniMax-M3 - Token Plan usage limit reached")
    expect(result.providerError?.message).toContain("Token Plan usage limit reached")
    expect(cancelled).toBe(true)

    const failed = (context.serverConnection as unknown as FakeServerConnection).calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)
      .find((payload) => payload.status === "failed")
    expect(failed?.failureReason).toBe("provider_error")
    expect((failed?.providerError as Record<string, unknown>)?.message).toContain("Token Plan usage limit reached")
  })

  it("SocketProviderDiagnostic_MonitorDoesNotFailFastBeforePromptTimeout", async () => {
    useAcpFakeTimers()
    useAcpProviderDiagnostic(socketDiagnostic(new Date().toISOString()))
    const promptStarted = deferred<void>()
    const connection = {
      async prompt() {
        promptStarted.resolve()
        return await new Promise(() => {})
      },
      async cancel() {
        return {}
      },
    }

    const action = monitorPrompt(baseContext({ prompt: "hanging task" }), connection as never, "fake-session-1", "do the work", {
      timeoutMs: 40,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      livenessState: createSessionLivenessState(),
      waitForData: () => new Promise<"data">(() => {}),
    })
    await promptStarted.promise
    await vi.advanceTimersByTimeAsync(40)
    const result = await action

    expect(result).not.toBe("completed")
    if (result === "completed") return
    expect(result.failureReason).toBe("prompt_timeout")
    expect(result.error).toContain("Timed out after")
  })
})

describe("mohist/acp-agent cancelAndReturn bounded cleanup", () => {
  it("EphemeralSessionCancelHangs_CleanupForcesProcessKill", async () => {
    useAcpFakeTimers()
    const agent = new FakeAcpAgent("cancel-hangs")
    const tracked = createTrackedFakeProcess(agent, { hangCancelWrites: true })
    setAcpProcessFactoryForTest(() => tracked)
    const serverConnection = new FakeServerConnection()

    const result = await runWithDefaultModelWarning("work-1", () => acpAgentAction({
      ...baseContext({ prompt: "hanging task", timeout: 100 }),
      serverConnection: serverConnection as unknown as ServerConnection,
    }), async (action) => {
      await agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(100)
      await tracked.waitForCancelWrite()
      await vi.advanceTimersByTimeAsync(50)
      return action
    })

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Timed out")
    expect(tracked.cleanupCount()).toBeGreaterThanOrEqual(1)
  })

  it("EphemeralSessionCancelResolvesPromptly_NoForceCleanupFromTimeoutRace", async () => {
    useAcpFakeTimers()
    const agent = new FakeAcpAgent("cancel-hangs")
    agent.cancelHangs = false
    const tracked = createTrackedFakeProcess(agent)
    setAcpProcessFactoryForTest(() => tracked)
    const serverConnection = new FakeServerConnection()
    const controller = new AbortController()

    const cleanupBefore = tracked.cleanupCount()
    const action = runWithDefaultModelWarning("work-1", () => acpAgentAction({
      ...baseContext({ prompt: "abort task", timeout: 5_000 }, controller.signal),
      serverConnection: serverConnection as unknown as ServerConnection,
    }))
    await agent.waitForPrompt()
    controller.abort()
    const result = await action

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
    const extraCleanups = tracked.cleanupCount() - cleanupBefore
    expect(extraCleanups).toBeLessThanOrEqual(1)
  })

  it("SharedSessionCancelHangs_NoProcessIsKilled", async () => {
    useAcpFakeTimers()
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { runtimeSessionId: "server-session-1", runtime: "opencode" } })
    shared.agent.cancelHangs = true

    const result = await runWithDefaultModelWarning("shared-session", () => acpAgentAction(contextWithOverrides({
      prompt: "long shared task",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 100,
    }, undefined, shared.context())), async (action) => {
      await shared.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(100)
      return action
    })

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Timed out")
    expect(shared.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
  })
})

describe("mohist/acp-agent monitorPrompt prompt_timeout diagnostics", () => {
  it("PromptTimesOutWithProviderDiagnostic_ErrorMessageContainsDiagnostic_AndFailureCategoryIsPromptTimeout", async () => {
    useAcpFakeTimers()
    useAcpProviderDiagnostic(tokenPlanLimitDiagnostic("2026-06-03T16:49:06.000Z"))
    const fixture = createFixture("cancel-hangs")
    setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

    const result = await runWithDefaultModelWarning("work-1", () => acpAgentAction(fixture.context({
      prompt: "hanging prompt",
      timeout: 100,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(100)
      return action
    })

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
  })

  it("PromptTimesOutWithoutProviderDiagnostic_ErrorContainsNoDiagnostic_AndFailureCategoryStillPromptTimeout", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("cancel-hangs")
    setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

    const result = await runWithDefaultModelWarning("work-1", () => acpAgentAction(fixture.context({
      prompt: "hanging prompt no log",
      timeout: 100,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(100)
      return action
    })

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
  })

  it("PromptWithoutExplicitTimeout_UsesOneHourDefault", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("cancel-hangs")
    setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

    const result = await runWithDefaultModelWarning("work-1", () => acpAgentAction(fixture.context({
      prompt: "hanging prompt default timeout",
      livenessQuietThresholdMs: 2 * 60 * 60 * 1000,
      probeTimeoutMs: 5_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(60 * 60 * 1000)
      return action
    })

    expect(result.status).toBe("failure")
    expect(result.message).toContain("Timed out after 3600s")
  })

  it("PromptTimesOut_EmitsLivenessFailedEventWithPromptTimeoutFailureReason", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("cancel-hangs")
    setAcpProcessFactoryForTest(() => createFakeProcess(fixture.agent))

    await runWithDefaultModelWarning("work-1", () => acpAgentAction(fixture.context({
      prompt: "hanging prompt liveness event",
      timeout: 100,
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(100)
      return action
    })

    const livenessEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)
    const failed = livenessEvents.find((payload) => payload.status === "failed")
    expect(failed).toBeTruthy()
    expect(failed?.failureReason).toBe("prompt_timeout")
    expect(failed?.runtimeSessionId).toBe("fake-session-1")
  })
})

async function runWithDefaultModelWarning<T>(
  sessionName: string,
  operation: () => Promise<T>,
  drive?: (action: Promise<T>) => Promise<T>,
): Promise<T> {
  const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
  try {
    const action = operation()
    const result = drive === undefined ? await action : await drive(action)
    expect(warningSpy).toHaveBeenCalledTimes(1)
    expect(warningSpy).toHaveBeenNthCalledWith(
      1,
      "mohist acp model not configured; using provider default",
      {
        workflowRunId: "workflow-1",
        workId: "work-1",
        stage: "build",
        sessionName,
        requestedModel: null,
        requestedModelSource: "none",
      },
    )
    return result
  } finally {
    warningSpy.mockRestore()
  }
}

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

function tokenPlanDiagnostic(occurredAt: string): OpencodeProviderErrorDiagnostic {
  return {
    sessionId: "fake-session-1",
    summary: "Opencode provider error: AI_APICallError on minimax-coding-plan/MiniMax-M3 - Token Plan usage limit reached",
    providerId: "minimax-coding-plan",
    modelId: "MiniMax-M3",
    errorName: "AI_APICallError",
    message: "Token Plan usage limit reached",
    occurredAt,
  }
}

function socketDiagnostic(occurredAt: string): OpencodeProviderErrorDiagnostic {
  return {
    sessionId: "fake-session-1",
    summary: "Opencode provider error: AI_APICallError on minimax-coding-plan/MiniMax-M3 - Cannot connect to API: The socket connection was closed unexpectedly.",
    providerId: "minimax-coding-plan",
    modelId: "MiniMax-M3",
    errorName: "AI_APICallError",
    message: "Cannot connect to API: The socket connection was closed unexpectedly.",
    occurredAt,
  }
}

function tokenPlanLimitDiagnostic(occurredAt: string): OpencodeProviderErrorDiagnostic {
  return {
    sessionId: "fake-session-1",
    summary: "Opencode provider error: 2056 token_plan_limit_error on minimax-coding-plan/MiniMax-M3 - Token Plan usage limit reached",
    providerId: "minimax-coding-plan",
    modelId: "MiniMax-M3",
    statusCode: 2056,
    errorName: "AI_APICallError",
    errorType: "token_plan_limit_error",
    message: "Token Plan usage limit reached",
    retryable: true,
    occurredAt,
  }
}
