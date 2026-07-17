import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import type { OpencodeProviderErrorDiagnostic } from "../../src/runtime/opencode-log-diagnostics.js"
import {
  createFixture,
  createSharedSessionFixture,
  resetAcpTestHooks,
  useAcpProviderDiagnostic,
  useAcpFakeTimers,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/acp-agent strategy liveness routing", () => {
  it("RunningSessionReceivesToolActivityAfterProbe_LivenessRecovers_WithExplainableMetadata", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("tool-liveness")

    const result = await runWithDefaultModelWarning("tool-session", () => acpAgentAction(fixture.context({
      prompt: "long tool task",
      session: "tool-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 80,
      timeout: 1_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(30)
      await vi.advanceTimersByTimeAsync(20)
      return action
    })

    expect(result.status).toBe("success")

    const livenessEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)

    const probing = livenessEvents.find((payload) => payload.status === "probing")
    expect(probing).toBeTruthy()
    expect(probing?.probeSentAt).toEqual(expect.any(String))
    expect(probing?.probeDeadlineAt).toEqual(expect.any(String))
    expect(probing?.lastDataAt).toEqual(expect.any(String))
    expect(probing?.activeProbeVersion).toEqual(expect.any(Number))
    expect(probing?.probeVersion).toBe(probing?.activeProbeVersion)

    const recovered = livenessEvents.find((payload) => payload.status === "running")
    expect(recovered).toBeTruthy()
    expect(recovered?.lastDataAt).toEqual(expect.any(String))
    expect(["tool_call", "tool_call_update", "tool_result", "tool_result_update"]).toContain(recovered?.lastActivityType)
    expect(recovered?.satisfiedProbeVersion).toBe(probing?.activeProbeVersion)

    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "tool_call.started")).toBe(true)
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "tool_call.completed")).toBe(true)
    expect(result.message ?? "").not.toContain("Session liveness probe timed out")
  })

  it("RunningSessionStaysQuietAfterProbe_LivenessTimeoutFails_WithProbeMetadata", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("probe-timeout")

    const result = await runWithDefaultModelWarning("timeout-session", () => acpAgentAction(fixture.context({
      prompt: "quiet task",
      session: "timeout-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(30)
      await vi.advanceTimersByTimeAsync(60)
      return action
    })

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Session liveness probe timed out")

    const livenessEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)

    const probing = livenessEvents.find((payload) => payload.status === "probing")
    const failed = livenessEvents.find((payload) => payload.status === "failed")

    expect(probing).toBeTruthy()
    expect(failed).toBeTruthy()
    expect(failed?.failureReason).toBe("probe_timeout")
    expect(failed?.probeSentAt).toBe(probing?.probeSentAt)
    expect(failed?.probeDeadlineAt).toBe(probing?.probeDeadlineAt)
    expect(failed?.activeProbeVersion).toBe(probing?.activeProbeVersion)
    expect(failed?.probeVersion).toBe(probing?.activeProbeVersion)
    expect(failed?.postProbeActivity).toBe(false)
    expect(failed?.lastDataAt).toEqual(expect.any(String))
    expect(failed?.lastActivityType).toEqual(expect.any(String))

    const probeJson = (result.message ?? "").slice((result.message ?? "").indexOf("{")).split("\n", 1)[0]
    const probeState = JSON.parse(probeJson) as Record<string, unknown>
    expect(probeState.probeSentAt).toBe(probing?.probeSentAt)
    expect(probeState.probeDeadlineAt).toBe(probing?.probeDeadlineAt)
    expect(probeState.probeVersion).toBe(probing?.activeProbeVersion)
    expect(probeState.postProbeActivity).toBe(false)
    expect(Number(probeState.dataVersion)).toBe(Number(probeState.probeVersion))
  })

  it("ProbeTimeoutWithRecoverableProviderError_DoesNotAttributeProviderError", async () => {
    useAcpFakeTimers()
    useAcpProviderDiagnostic(contextOverflowDiagnostic())
    const fixture = createFixture("probe-timeout")

    const result = await runWithDefaultModelWarning("timeout-session", () => acpAgentAction(fixture.context({
      prompt: "quiet task",
      session: "timeout-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    })), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(30)
      await vi.advanceTimersByTimeAsync(60)
      return action
    })

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Session liveness probe timed out")
    expect(result.message ?? "").not.toContain("Opencode provider error")

    const failed = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)
      .find((payload) => payload.status === "failed")
    const terminal = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)

    expect(failed?.providerError).toBeUndefined()
    expect(failed?.failureReason).toBe("probe_timeout")
    expect(String(terminal?.failureReason)).not.toContain("Opencode provider error")
  })

  it("ProbePromptSendRejects_LivenessFailsAsProbeSendFailed_InsteadOfTimingOut", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("basic")
    const shared = createSharedSessionFixture("probe-send-failed")

    const result = await runWithDefaultModelWarning("probe-send-failed-session", () => acpAgentAction(fixture.context({
      prompt: "probe send fails",
      session: "probe-send-failed-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    }, undefined, shared.context())), async (action) => {
      await shared.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(30)
      return action
    })

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Failed to send liveness probe: probe transport failed")
    expect(result.message ?? "").not.toContain("Session liveness probe timed out")

    const livenessEvents = shared.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
      .map((entry) => entry.payload as Record<string, unknown>)

    const probing = livenessEvents.find((payload) => payload.status === "probing")
    const failed = livenessEvents.find((payload) => payload.status === "failed")

    expect(probing).toBeTruthy()
    expect(failed).toBeTruthy()
    expect(failed?.failureReason).toBe("probe_send_failed")
    expect(failed?.activeProbeVersion).toBe(probing?.activeProbeVersion)
    expect(failed?.probeSentAt).toBe(probing?.probeSentAt)
    expect(failed?.probeDeadlineAt).toBe(probing?.probeDeadlineAt)
  })

  it("AbortSignalFiresDuringProbe_CancellationRemainsDistinctFromLivenessFailure", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("abort-during-probe")
    const controller = new AbortController()

    const action = runWithDefaultModelWarning("cancel-session", () => acpAgentAction(fixture.context({
      prompt: "cancel during probe",
      session: "cancel-session",
      livenessQuietThresholdMs: 20,
      probeTimeoutMs: 200,
      timeout: 1_000,
    }, controller.signal)))
    await fixture.agent.waitForPrompt()
    await vi.advanceTimersByTimeAsync(20)
    await fixture.serverConnection.waitForLivenessProbe()
    controller.abort()
    const result = await action

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(fixture.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness" && (entry.payload as { failureReason?: string }).failureReason === "probe_timeout")).toBe(false)
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

function contextOverflowDiagnostic(): OpencodeProviderErrorDiagnostic {
  return {
    sessionId: "fake-session-1",
    summary: "Opencode provider error: AI_APICallError on openai/gpt-5.6-terra - Your input exceeds the context window of this model. Please adjust your input and try again.",
    providerId: "openai",
    modelId: "gpt-5.6-terra",
    errorName: "AI_APICallError",
    message: "Your input exceeds the context window of this model. Please adjust your input and try again.",
    occurredAt: "2026-06-30T00:00:00.500Z",
  }
}
