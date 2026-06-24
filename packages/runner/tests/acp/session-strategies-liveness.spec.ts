import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import {
  createFixture,
  createSharedSessionFixture,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  delete process.env.MOHIST_OPENCODE_LOG_DIR
})

describe("mohist/acp-agent strategy liveness routing", () => {
  it("RunningSessionReceivesToolActivityAfterProbe_LivenessRecovers_WithExplainableMetadata", async () => {
    const fixture = createFixture("tool-liveness")

    const result = await acpAgentAction(fixture.context({
      prompt: "long tool task",
      session: "tool-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 80,
      timeout: 1_000,
    }))

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
    expect(probing?.probeVersion).toEqual(probing?.activeProbeVersion)

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
    const fixture = createFixture("probe-timeout")

    const result = await acpAgentAction(fixture.context({
      prompt: "quiet task",
      session: "timeout-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    }))

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

  it("ProbeTimeoutWithOpencodeProviderError_AppendsProviderDiagnostic", async () => {
    const logDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = logDir
    try {
      await writeFile(join(logDir, "2026-06-03T164901.log"), [
        'ERROR 2026-06-03T16:49:06 service=llm providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=fake-session-1 small=false agent=build mode=primary error={"error":{"name":"AI_APICallError","statusCode":429,"responseBody":"{\\"type\\":\\"error\\",\\"error\\":{\\"type\\":\\"rate_limit_error\\",\\"message\\":\\"usage limit exceeded\\"}}","isRetryable":true}} stream error',
        "",
      ].join("\n"))
      const fixture = createFixture("probe-timeout")

      const result = await acpAgentAction(fixture.context({
        prompt: "quiet task",
        session: "timeout-session",
        livenessQuietThresholdMs: 30,
        probeTimeoutMs: 60,
        timeout: 1_000,
      }))

      expect(result.status).toBe("failure")
      expect(result.message ?? "").toContain("Session liveness probe timed out")
      expect(result.message ?? "").toContain("Opencode provider error: 429 rate_limit_error on minimax-coding-plan/MiniMax-M3 - usage limit exceeded")

      const failed = fixture.serverConnection.calls
        .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness")
        .map((entry) => entry.payload as Record<string, unknown>)
        .find((payload) => payload.status === "failed")
      const terminal = fixture.serverConnection.calls
        .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
        .map((entry) => entry.payload as Record<string, unknown>)
        .at(-1)

      expect((failed?.providerError as Record<string, unknown>)?.statusCode).toBe(429)
      expect((failed?.providerError as Record<string, unknown>)?.errorType).toBe("rate_limit_error")
      expect(String(terminal?.failureReason)).toContain("Opencode provider error: 429 rate_limit_error")
    } finally {
      await rm(logDir, { recursive: true, force: true })
    }
  })

  it("ProbePromptSendRejects_LivenessFailsAsProbeSendFailed_InsteadOfTimingOut", async () => {
    const fixture = createFixture("basic")
    const shared = createSharedSessionFixture("probe-send-failed")

    const result = await acpAgentAction(fixture.context({
      prompt: "probe send fails",
      session: "probe-send-failed-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    }, undefined, shared.context()))

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
    const fixture = createFixture("abort-during-probe")
    const controller = new AbortController()
    setTimeout(() => controller.abort(), 60)

    const result = await acpAgentAction(fixture.context({
      prompt: "cancel during probe",
      session: "cancel-session",
      livenessQuietThresholdMs: 20,
      probeTimeoutMs: 200,
      timeout: 1_000,
    }, controller.signal))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(fixture.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness" && (entry.payload as { failureReason?: string }).failureReason === "probe_timeout")).toBe(false)
  })
})
