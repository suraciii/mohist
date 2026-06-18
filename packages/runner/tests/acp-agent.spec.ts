import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it, vi } from "vitest"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, defaultCompactionConfig, resolveCompactionConfig, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../src/actions/acp-agent.js"
import type { ActionContext } from "../src/core/types.js"
import { AcpSessionManager, type SharedAcpConnection } from "../src/runtime/acp-connection.js"
import { ServerConnection } from "../src/server/connection.js"
import {
  PromptLoaderRegistry,
  setPromptLoaderRegistryForTest,
  type PromptLoader,
  type PromptLoaderContext,
} from "../src/core/prompt.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  delete process.env.MOHIST_OPENCODE_LOG_DIR
})

describe("mohist/acp-agent", () => {
  it("ValidAcpAgentWork_ActionRuns_SpawnsAcpAndInitializesSessionBeforePrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").acpSessionId).toBe("fake-session-1")
    expect(fixture.agent.calls.map((call) => call.event).filter((event) => ["initialize", "newSession", "prompt"].includes(event))).toEqual(["initialize", "newSession", "prompt"])
  })

  it("ModelConfigured_AcpSessionStarts_SetsSessionConfigModelBeforePrompt", async () => {
    const fixture = createFixture("basic")

    await acpAgentAction(fixture.context({ prompt: "do the work", model: "openai/gpt-4.1" }))

    expect(fixture.agent.calls.find((entry) => entry.event === "setSessionConfigOption" && entry.configId === "model" && entry.value === "openai/gpt-4.1")).toBeTruthy()
    expect(fixture.agent.calls.findIndex((entry) => entry.event === "setSessionConfigOption")).toBeLessThan(fixture.agent.calls.findIndex((entry) => entry.event === "prompt"))
  })

  it("SessionConfigModelFails_ModelConfigured_FallsBackToUnstableSetSessionModel", async () => {
    const fixture = createFixture("model-fallback")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work", model: "anthropic/claude" }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude")).toBeTruthy()
  })

  it("NewSessionCreatedBeforeModelConfiguration_RunnerReportsPhysicalSessionIdToServer", async () => {
    const fixture = createFixture("model-config-fails")

    const result = await acpAgentAction(fixture.context({
      prompt: "do the work",
      session: "build",
      model: "anthropic/claude",
    }))

    expect(result.status).toBe("success")
    expect(fixture.serverConnection.calls).toContainEqual(expect.objectContaining({
      event: "attachWorkflowAgentSession",
      sessionName: "build",
      body: expect.objectContaining({ agentSessionId: "fake-session-1" }),
    }))
    expect(fixture.timeline.findIndex((entry) => entry.event === "newSession")).toBeLessThan(fixture.timeline.findIndex((entry) => entry.event === "attachWorkflowAgentSession"))
    expect(fixture.timeline.findIndex((entry) => entry.event === "attachWorkflowAgentSession")).toBeLessThan(fixture.timeline.findIndex((entry) => entry.event === "setSessionConfigOption"))
  })

  it("PermissionRequestHasAllowOption_AgentRequestsPermission_SelectsAllowOption", async () => {
    const fixture = createFixture("permission")

    const result = await acpAgentAction(fixture.context({ prompt: "needs permission" }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.find((entry) => entry.event === "permissionResponse" && entry.outcome?.optionId === "allow")).toBeTruthy()
  })

  it("AgentMessageChunkArrives_SessionUpdateHandled_ReturnsAgentTextInOutput", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").text).toBe("hello")
  })

  it("ToolEventMissingToolNameButHasProviderId_ToolCallUpdateHandled_InfersToolNameAndReusesToolCallId", async () => {
    const fixture = createFixture("tool-weird")

    const result = await acpAgentAction(fixture.context({ prompt: "use tools" }))

    expect(result.status).toBe("success")
  })

  it("RunningSessionExceedsQuietThreshold_LivenessMonitored_EntersProbingAndSendsProbePrompt", async () => {
    const fixture = createFixture("liveness")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt" && entry.promptCount === 2 && entry.text.includes("still alive"))).toBe(true)
  })

  it("PromptCompletesWithoutSessionActivity_ActionFailsInsteadOfReportingEmptySuccess", async () => {
    const fixture = createFixture("empty-complete")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("failure")
    expect(result.message).toContain("without any session activity")
  })

  it("ExpectedArtifactMissing_AgentIsAskedToRepairArtifactBeforeTaskFails", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-acp-expect-"))
    const fixture = createFixture("expectation-repair")

    try {
      const result = await acpAgentAction(fixture.context({
        prompt: "review the change",
        session: "check",
        expect: {
          markers: [
            {
              path: "review.md",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
            },
          ],
        },
      }, undefined, { workDir }))

      expect(result.status).toBe("success")
      const promptCalls = fixture.agent.calls.filter((entry) => entry.event === "prompt")
      expect(promptCalls).toHaveLength(2)
      expect(promptCalls[1].text).toContain("did not satisfy this task's completion requirements")
      expect(promptCalls[1].text).toContain("review.md")
    } finally {
      await rm(workDir, { recursive: true, force: true })
    }
  })

  it("ExpectationRepairDisabled_MissingArtifactFailsWithoutFollowUpPrompt", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-acp-expect-disabled-"))
    const fixture = createFixture("basic")

    try {
      const result = await acpAgentAction(fixture.context({
        prompt: "review the change",
        expectationRepairLimit: 0,
        expect: {
          markers: [
            {
              path: "review.md",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
            },
          ],
        },
      }, undefined, { workDir }))

      expect(result.status).toBe("failure")
      expect(result.message).toContain("missing artifact marker")
      expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    } finally {
      await rm(workDir, { recursive: true, force: true })
    }
  })

  it("ExpectationRepairPromptOnlyReceivesUsage_ActionFailsAsNoSessionActivity", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-acp-expect-usage-only-"))
    const fixture = createFixture("expectation-repair-usage-only")

    try {
      const result = await acpAgentAction(fixture.context({
        prompt: "review the change",
        expect: {
          markers: [
            {
              path: "review.md",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
            },
          ],
        },
      }, undefined, { workDir }))

      expect(result.status).toBe("failure")
      expect(result.message).toContain("without any session activity")
      expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(2)
    } finally {
      await rm(workDir, { recursive: true, force: true })
    }
  })

  it("ProbeTimesOutWithoutQualifyingActivity_LivenessMonitored_FailsSession", async () => {
    const fixture = createFixture("quiet-then-done")

    const result = await acpAgentAction(fixture.context({ prompt: "long silent task", livenessQuietThresholdMs: 30, probeTimeoutMs: 30, timeout: 2_000 }))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Session liveness probe timed out")
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt" && entry.promptCount === 2 && entry.text.includes("still alive"))).toBe(true)
  })

  it("ThoughtAndToolUpdatesArrive_LivenessMonitored_DoNotProbeWhileAgentIsActive", async () => {
    const fixture = createFixture("liveness-non-message")

    const result = await acpAgentAction(fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
  })

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

  it("ResumedSharedSessionStreamsThoughtChunks_ProbeWindowCrossed_DoesNotTimeoutOrAppendThoughtText", async () => {
    const shared = createSharedSessionFixture("thought-liveness", { sessionRecord: { acpSessionId: "server-session-1" } })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "long resumed task",
      session: "shared-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 80,
      timeout: 1_000,
    }, undefined, shared.context()))

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

  it("AbortSignalFires_PromptRunning_SendsSessionCancelBeforeCleanup", async () => {
    const fixture = createFixture("abort")
    const controller = new AbortController()
    setTimeout(() => controller.abort(), 50)

    const result = await acpAgentAction(fixture.context({ prompt: "cancel me", timeout: 500 }, controller.signal))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(fixture.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
  })

  it("StringPrompt_ActionSendsPromptVerbatimWithoutMarkdownEnvelope", async () => {
    const fixture = createFixture("basic")

    const literal = "Fix the build-stage health failure reported by `git diff --check`.\n\n## Keep this markdown verbatim"
    const result = await acpAgentAction(fixture.context({ prompt: literal }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe(literal)
    expect(sentText).not.toContain("## Mohist Issue Context")
    expect(sentText).not.toContain("## Task Prompt")
  })

  it("StringPrompt_ActionDoesNotInjectIssueTitleOrBody", async () => {
    const fixture = createFixture("basic")

    const literal = "Resolve exactly this declared prompt."
    const issueTitle = "Distinct issue title that must not reach prompt text"
    const issueBody = "Distinct issue body that must not reach prompt text"
    const result = await acpAgentAction(fixture.context({ prompt: literal }, undefined, {
      issueNumber: 138,
      variables: {
        project: { path: "D:/fake/work" },
        issue: {
          number: 138,
          title: issueTitle,
          body: issueBody,
        },
      } as never,
    }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe(literal)
    expect(sentText).not.toContain(issueTitle)
    expect(sentText).not.toContain(issueBody)
  })

  it("StringPromptContainingLiteralTemplateSyntax_IsNotTemplateRenderedBeforeMohistContextWrapper", async () => {
    const fixture = createFixture("basic")

    const literal = "literal ${{ prompts.xxx }} should stay intact"
    const result = await acpAgentAction(fixture.context({ prompt: literal }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toContain(literal)
    expect(sentText).not.toContain("prompts.xxx".replace("xxx", "build"))
  })

  it("ObjectPrompt_ActionSendsRenderedXmlWithoutMarkdownEnvelope", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      prompt: {
        artifact: {
          attrs: { id: "build-task" },
          task: "Complete exactly one implementation task.",
          instruction: "Follow acceptance criteria.",
        },
      },
    }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe([
      `<artifact id="build-task">`,
      ``,
      `  <task>Complete exactly one implementation task.</task>`,
      ``,
      `  <instruction>Follow acceptance criteria.</instruction>`,
      ``,
      `</artifact>`,
    ].join("\n"))
    expect(sentText).not.toContain("## Task Prompt")
  })

  it("UsesFormPrompt_ActionResolvesThroughRegisteredLoaderBeforeMohistContextWrapper", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "loader produced task prompt")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/loader", loader)
    setPromptLoaderRegistryForTest(registry)

    const result = await acpAgentAction(fixture.context({
      prompt: { uses: "fake/loader", with: { file: "tasks.json", taskId: "T-001" } },
    }))

    expect(result.status).toBe("success")
    expect(loader).toHaveBeenCalledTimes(1)
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe("loader produced task prompt")
  })

  it("UsesFormPrompt_LoaderReturningObject_IsRenderedThroughDefaultRenderer", async () => {
    const fixture = createFixture("basic")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/object-loader", async () => ({
      artifact: { task: "rendered from loader" },
    }))
    setPromptLoaderRegistryForTest(registry)

    const result = await acpAgentAction(fixture.context({ prompt: { uses: "fake/object-loader" } }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toBe([
      `<artifact>`,
      ``,
      `  <task>rendered from loader</task>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("UsesFormPrompt_LoaderReceivesContextWithWorkflowVariablesWorkDirWorkIdTitleAndStage", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "ok")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    const variables = {
      workflow: { name: "build" },
      project: { path: "D:/fake/work" },
    }
    await acpAgentAction(fixture.context({
      prompt: { uses: "fake/echo-loader", with: { file: "tasks.json", taskId: "T-001" } },
    }, new AbortController().signal, {
      variables: variables as never,
      stage: "build",
      title: "Build task",
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0] as PromptLoaderContext
    expect(received.with).toEqual({ file: "tasks.json", taskId: "T-001" })
    expect(received.variables).toEqual(variables)
    expect(received.workDir).toBe("D:/fake/work")
    expect(received.workId).toBe("work-1")
    expect(received.title).toBe("Build task")
    expect(received.stage).toBe("build")
  })

  it("UsesFormPrompt_LoaderReceivesContextWithNullTitleAndStageWhenAbsent", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "ok")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    await acpAgentAction(fixture.context({ prompt: { uses: "fake/echo-loader" } }, new AbortController().signal, {
      title: null,
      stage: null,
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0] as PromptLoaderContext
    expect(received.title).toBeNull()
    expect(received.stage).toBeNull()
  })

  it("MissingPrompt_ActionFailsWithoutSendingSynthesizedPrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      description: "Requeue runnable workflows on server startup.",
      acceptanceCriteria: ["runner can claim recovered work"],
    }))

    expect(result.status).toBe("failure")
    expect(result.message).toBe("ACP agent requires 'prompt'")
    expect(fixture.agent.calls.find((entry) => entry.event === "prompt")).toBeUndefined()
    expect(fixture.agent.calls.find((entry) => entry.event === "initialize")).toBeUndefined()
  })

  it("UnknownPromptLoader_ActionFailsWithClearErrorBeforeAnyAcpInteraction", async () => {
    const fixture = createFixture("basic")
    setPromptLoaderRegistryForTest(new PromptLoaderRegistry())

    const result = await acpAgentAction(fixture.context({ prompt: { uses: "no/such-loader" } }))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Unknown prompt loader: 'no/such-loader'")
    expect(fixture.agent.calls.find((entry) => entry.event === "initialize")).toBeUndefined()
  })

  it("NewSessionReturnsCurrentModelId_RunnerEmitsResolvedModelEvent", async () => {
    const fixture = createFixture("resolved-model")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    const resolvedModelEvent = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "model.resolved")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(resolvedModelEvent).toBeTruthy()
    expect(resolvedModelEvent?.resolvedModel).toBe("openai/gpt-4.1")
    expect(resolvedModelEvent?.source).toBe("newSession")
    expect(resolvedModelEvent?.acpSessionId).toBe("fake-session-1")
  })

  it("NewSessionLacksCurrentModelId_RunnerDoesNotEmitResolvedModelEvent", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "model.resolved")).toBe(false)
  })

  it("ConfigOptionUpdateChangesModel_RunnerEmitsResolvedModelEvent", async () => {
    const fixture = createFixture("config-option-update")

    const result = await acpAgentAction(fixture.context({ prompt: "switch the model" }))

    expect(result.status).toBe("success")
    const resolvedModelEvent = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "model.resolved")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(resolvedModelEvent).toBeTruthy()
    expect(resolvedModelEvent?.resolvedModel).toBe("anthropic/claude-sonnet-4-5")
    expect(resolvedModelEvent?.source).toBe("config_option_update")
  })

  it("UsageUpdateArrives_RunnerEmitsAgentUsageUpdateAndPreservesLiveness", async () => {
    const fixture = createFixture("usage-update")

    const result = await acpAgentAction(fixture.context({ prompt: "track usage" }))

    expect(result.status).toBe("success")
    const usageEvent = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "usage.updated")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(usageEvent).toBeTruthy()
    expect(usageEvent?.source).toBe("usage_update")
    expect(usageEvent?.contextWindowSize).toBe(200000)
    expect(usageEvent?.contextWindowUsed).toBe(15000)
    expect(usageEvent?.costAmount).toBe(0.0012)
    expect(usageEvent?.costCurrency).toBe("USD")
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.liveness" && (entry.payload as { failureReason?: string }).failureReason === "probe_timeout")).toBe(false)
  })

  it("PromptResponseCarriesUsage_RunnerEmitsAgentUsageUpdateAfterCompletion", async () => {
    const fixture = createFixture("prompt-usage")

    const result = await acpAgentAction(fixture.context({ prompt: "report usage" }))

    expect(result.status).toBe("success")
    const usageEvent = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "usage.updated")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(usageEvent).toBeTruthy()
    expect(usageEvent?.source).toBe("prompt_response")
    expect(usageEvent?.inputTokens).toBe(120)
    expect(usageEvent?.outputTokens).toBe(40)
    expect(usageEvent?.totalTokens).toBe(160)
    expect(usageEvent?.cachedReadTokens).toBe(80)
    expect(usageEvent?.thoughtTokens).toBe(5)

    const promptEventIndex = fixture.serverConnection.calls.findIndex((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.input")
    const usageEventIndex = fixture.serverConnection.calls.findIndex((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "usage.updated")
    const terminalEventIndex = fixture.serverConnection.calls.findIndex((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
    expect(promptEventIndex).toBeGreaterThanOrEqual(0)
    expect(usageEventIndex).toBeGreaterThan(promptEventIndex)
    expect(terminalEventIndex).toBeGreaterThan(usageEventIndex)
  })

  it("ProbeTimeoutFails_TerminalEventCarriesProbeTimeoutFailureCategory", async () => {
    const fixture = createFixture("probe-timeout")

    const result = await acpAgentAction(fixture.context({
      prompt: "quiet task",
      session: "timeout-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    }))

    expect(result.status).toBe("failure")
    const terminalEvent = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(terminalEvent).toBeTruthy()
    expect(terminalEvent?.failureCategory).toBe("probe_timeout")
    expect(terminalEvent?.failureReason).toEqual(expect.any(String))
  })

  it("SuccessfulRun_TerminalEventOmitsFailureCategory", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "happy path" }))

    expect(result.status).toBe("success")
    const terminalEvent = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "session.closed")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(terminalEvent).toBeTruthy()
    expect(terminalEvent?.status).toBe("completed")
    expect(terminalEvent?.failureCategory).toBeNull()
    expect(terminalEvent?.failureReason).toBeNull()
  })

  it("CompactionConfigNotSpecified_DefaultsApplied_NewSessionReceivesOpencodeCompactionMeta", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    const newSessionCall = fixture.agent.calls.find((entry) => entry.event === "newSession")
    expect(newSessionCall).toBeTruthy()
    const meta = newSessionCall?._meta as Record<string, unknown> | undefined
    expect(meta).toBeTruthy()
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("CompactionConfigExplicitlySet_ForwardedToNewSessionMeta", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      prompt: "do the work",
      compaction: { threshold: 0.7, strategy: "summary" },
    }))

    expect(result.status).toBe("success")
    const newSessionCall = fixture.agent.calls.find((entry) => entry.event === "newSession")
    const meta = newSessionCall?._meta as Record<string, unknown> | undefined
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.7, strategy: "summary" })
  })

  it("CompactionConfigNestedUnderAgent_ForwardedToNewSessionMeta", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      prompt: "do the work",
      agent: { compaction: { threshold: 0.6, strategy: "summary" } },
    }))

    expect(result.status).toBe("success")
    const newSessionCall = fixture.agent.calls.find((entry) => entry.event === "newSession")
    const meta = newSessionCall?._meta as Record<string, unknown> | undefined
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.6, strategy: "summary" })
  })

  it("CompactionThresholdOutOfRange_DefaultsToValidRange_Forwarded", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      prompt: "do the work",
      compaction: { threshold: 1.5, strategy: "summary" },
    }))

    expect(result.status).toBe("success")
    const newSessionCall = fixture.agent.calls.find((entry) => entry.event === "newSession")
    const meta = newSessionCall?._meta as Record<string, unknown> | undefined
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction?.threshold).toBe(0.8)
  })

  it("CompactionStrategyUnsupported_FallsBackToSummary", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      prompt: "do the work",
      compaction: { threshold: 0.5, strategy: "unknown" as never },
    }))

    expect(result.status).toBe("success")
    const newSessionCall = fixture.agent.calls.find((entry) => entry.event === "newSession")
    const meta = newSessionCall?._meta as Record<string, unknown> | undefined
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction?.strategy).toBe("summary")
  })

  it("CompactionEventArrives_RunnerEmitsUsageUpdatedEventWithBeforeAfterMetrics", async () => {
    const fixture = createFixture("compaction")

    const result = await acpAgentAction(fixture.context({ prompt: "trigger compaction" }))

    expect(result.status).toBe("success")
    const usageEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "usage.updated")
      .map((entry) => entry.payload as Record<string, unknown>)
    const compactionEvent = usageEvents.find((payload) => payload.source === "compaction")
    expect(compactionEvent).toBeTruthy()
    expect(compactionEvent?.contextWindowUsedBefore).toBe(180000)
    expect(compactionEvent?.contextWindowUsedAfter).toBe(60000)
    expect(compactionEvent?.contextWindowSize).toBe(200000)
    expect(compactionEvent?.compactionStrategy).toBe("summary")
  })

  it("CompactionEventArrives_RunnerEmitsDedicatedCompactionEvent", async () => {
    const fixture = createFixture("compaction")

    const result = await acpAgentAction(fixture.context({ prompt: "trigger compaction" }))

    expect(result.status).toBe("success")
    const compactionEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "compaction")
      .map((entry) => entry.payload as Record<string, unknown>)
    expect(compactionEvents.length).toBeGreaterThan(0)
    const payload = compactionEvents[0]
    expect(payload?.contextWindowUsedBefore).toBe(180000)
    expect(payload?.contextWindowUsedAfter).toBe(60000)
    expect(payload?.contextWindowSize).toBe(200000)
    expect(payload?.strategy).toBe("summary")
  })

  it("CompactionEventUpdatesContextWindowSizeInUsageUpdate", async () => {
    const fixture = createFixture("compaction")

    const result = await acpAgentAction(fixture.context({ prompt: "trigger compaction" }))

    expect(result.status).toBe("success")
    const usageEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "usage.updated")
      .map((entry) => entry.payload as Record<string, unknown>)
    const finalUsage = usageEvents[usageEvents.length - 1]
    expect(finalUsage?.contextWindowSize).toBe(200000)
    expect(finalUsage?.contextWindowUsed).toBe(60000)
  })
})

describe("mohist/acp-agent shared session observability", () => {
  it("ResumedSessionExposesCurrentModelId_RunnerEmitsResolvedModelEventWithResumeSource", async () => {
    const shared = createSharedSessionFixture("resolved-model", { sessionRecord: { acpSessionId: "server-session-1" } })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "resume and report",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context()))

    expect(result.status).toBe("success")
    const resolvedModelEvent = shared.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "model.resolved")
      .map((entry) => entry.payload as Record<string, unknown>)
      .at(-1)
    expect(resolvedModelEvent).toBeTruthy()
    expect(resolvedModelEvent?.resolvedModel).toBe("anthropic/claude-haiku-4-5")
    expect(resolvedModelEvent?.source).toBe("resumeSession")
  })

  it("CompactionConfigNotSpecified_DefaultsApplied_ResumeSessionReceivesOpencodeCompactionMeta", async () => {
    const shared = createSharedSessionFixture("resolved-model", { sessionRecord: { acpSessionId: "server-session-1" } })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "resume with defaults",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context()))

    expect(result.status).toBe("success")
    const resumeCall = shared.agent.calls.find((entry) => entry.event === "resumeSession")
    expect(resumeCall).toBeTruthy()
    const meta = resumeCall?._meta as Record<string, unknown> | undefined
    expect(meta).toBeTruthy()
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("CompactionConfigExplicitlySet_ForwardedToResumeSessionMeta", async () => {
    const shared = createSharedSessionFixture("resolved-model", { sessionRecord: { acpSessionId: "server-session-1" } })

    const result = await acpAgentAction(contextWithOverrides({
      prompt: "resume with custom compaction",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
      compaction: { threshold: 0.65, strategy: "summary" },
    }, undefined, shared.context()))

    expect(result.status).toBe("success")
    const resumeCall = shared.agent.calls.find((entry) => entry.event === "resumeSession")
    const meta = resumeCall?._meta as Record<string, unknown> | undefined
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.65, strategy: "summary" })
  })
})

describe("mohist/acp-agent compaction config helpers", () => {
  it("defaultCompactionConfig_ReturnsThresholdZeroPointEightAndSummaryStrategy", () => {
    expect(defaultCompactionConfig()).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("resolveCompactionConfig_WithNoAgentConfig_AppliesDefaults", () => {
    expect(resolveCompactionConfig(undefined)).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("resolveCompactionConfig_WithEmptyAgentConfig_AppliesDefaults", () => {
    expect(resolveCompactionConfig({})).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("resolveCompactionConfig_WithExplicitConfig_PassesThroughValues", () => {
    expect(resolveCompactionConfig({ compaction: { threshold: 0.5, strategy: "summary" } }))
      .toEqual({ threshold: 0.5, strategy: "summary" })
  })
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

function createFixture(scenario: Scenario) {
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

function createSharedFixture(scenario: Scenario) {
  const agent = new FakeAcpAgent(scenario)
  const [clientStream, agentStream] = linkedStreams()
  const agentConnection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(agentConnection)
  const server = fakeServerConnection()
  const sharedConnection = createSharedConnection(clientStream)

  return {
    agent,
    server,
    context(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
      return {
        ...baseContext(withInput, signal),
        acpSessionManager: new AcpSessionManager(),
        acpConnection: sharedConnection,
        serverConnection: server as never,
      }
    },
  }
}

function contextWithOverrides(withInput: Record<string, unknown>, signal = new AbortController().signal, overrides: Partial<ActionContext> = {}): ActionContext {
  return {
    ...baseContext(withInput, signal),
    ...overrides,
  }
}

function baseContext(withInput: Record<string, unknown>, signal = new AbortController().signal): ActionContext {
  return {
    workflowRunId: "workflow-1",
    workId: "work-1",
    workType: "task",
    stage: "build",
    title: "Build task",
    uses: "mohist/acp-agent",
    with: withInput as never,
    variables: {
      project: { path: "D:/fake/work" },
      issue: {
        number: 7,
        title: "Document update smoke validation note",
        body: "Add a short note that records the expected local post-update smoke validation path.",
      },
    } as never,
    workDir: "D:/fake/work",
    signal,
    projectId: "project-1",
    issueNumber: 7,
  }
}

type Scenario = "basic" | "model-fallback" | "model-config-fails" | "permission" | "tool-weird" | "liveness" | "quiet-then-done" | "liveness-non-message" | "abort" | "tool-liveness" | "probe-timeout" | "abort-during-probe" | "empty-complete" | "resolved-model" | "config-option-update" | "usage-update" | "prompt-usage" | "compaction" | "expectation-repair" | "expectation-repair-usage-only" | "cancel-hangs"

class FakeAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection
  private promptCount = 0
  private initialPromptResolve: ((value: { stopReason: "end_turn" }) => void) | null = null
  cancelHangs = false

  constructor(private readonly scenario: Scenario, private readonly timeline?: Array<{ event: string }>) {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize(params) {
        self.calls.push({ event: "initialize", protocolVersion: params.protocolVersion })
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession(params) {
        self.timeline?.push({ event: "newSession" })
        self.calls.push({ event: "newSession", cwd: params.cwd, _meta: params._meta })
        if (self.scenario === "resolved-model") {
          return { sessionId: "fake-session-1", models: { currentModelId: "openai/gpt-4.1" } }
        }
        return { sessionId: "fake-session-1" }
      },
      async resumeSession(params) {
        self.timeline?.push({ event: "resumeSession" })
        self.calls.push({ event: "resumeSession", sessionId: params.sessionId, cwd: params.cwd })
        return { sessionId: params.sessionId }
      },
      async setSessionConfigOption(params) {
        self.timeline?.push({ event: "setSessionConfigOption" })
        self.calls.push({ event: "setSessionConfigOption", ...params })
        if (self.scenario === "model-fallback" || self.scenario === "model-config-fails") throw new Error("set config unsupported")
        return { configOptions: [] }
      },
      async unstable_setSessionModel(params) {
        self.calls.push({ event: "unstable_setSessionModel", ...params })
        if (self.scenario === "model-config-fails") throw new Error("set model unsupported")
        return {}
      },
      async prompt(params) {
        self.promptCount += 1
        const text = params.prompt.map((part) => part.type === "text" ? part.text : "").join("\n")
        self.calls.push({ event: "prompt", promptCount: self.promptCount, text })
        if (self.scenario === "permission") {
          const response = await self.connection.requestPermission({ sessionId: params.sessionId, toolCall: { toolCallId: "tool-permission", title: "Run command", kind: "execute", status: "pending" }, options: [{ optionId: "reject", name: "Reject", kind: "reject_once" }, { optionId: "allow", name: "Allow", kind: "allow_once" }] })
          self.calls.push({ event: "permissionResponse", ...response })
        }
        if (self.scenario === "liveness") return await self.runLivenessPrompt(params.sessionId)
        if (self.scenario === "quiet-then-done") return await self.runQuietThenDonePrompt(params.sessionId)
        if (self.scenario === "liveness-non-message") return await self.runNonMessageLivenessPrompt(params.sessionId)
        if (self.scenario === "tool-liveness") return await self.runToolLivenessPrompt(params.sessionId)
        if (self.scenario === "probe-timeout") return await self.runProbeTimeoutPrompt()
        if (self.scenario === "abort-during-probe") return await self.runAbortDuringProbePrompt()
        if (self.scenario === "abort") return await new Promise(() => {})
        if (self.scenario === "cancel-hangs") return await new Promise(() => {})
        if (self.scenario === "empty-complete") return { stopReason: "end_turn" }
        if (self.scenario === "expectation-repair") return await self.runExpectationRepairPrompt(params.sessionId, text)
        if (self.scenario === "expectation-repair-usage-only") return await self.runExpectationRepairUsageOnlyPrompt(params.sessionId, text)
        if (self.scenario === "tool-weird") await self.emitWeirdToolEvents(params.sessionId)
        if (self.scenario === "config-option-update") {
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "agent_message_chunk", content: { type: "text", text: "switching" } } } as never)
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "config_option_update", configOptions: [{ id: "model", category: "model", name: "Model", type: "select", currentValue: "anthropic/claude-sonnet-4-5", options: [{ value: "anthropic/claude-sonnet-4-5", name: "Claude Sonnet 4.5" }] }] } } as never)
          return { stopReason: "end_turn" }
        }
        if (self.scenario === "usage-update") {
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "usage_update", size: 200000, used: 15000, cost: { amount: 0.0012, currency: "USD" } } } as never)
          return { stopReason: "end_turn" }
        }
        if (self.scenario === "prompt-usage") {
          await self.connection.sessionUpdate(textUpdate(params.sessionId, "usage test"))
          return { stopReason: "end_turn", usage: { inputTokens: 120, outputTokens: 40, totalTokens: 160, cachedReadTokens: 80, thoughtTokens: 5 } }
        }
        if (self.scenario === "compaction") {
          await self.connection.sessionUpdate(textUpdate(params.sessionId, "before-compact"))
          await self.connection.sessionUpdate({ sessionId: params.sessionId, update: { sessionUpdate: "usage_update", size: 200000, used: 60000, _meta: { "opencode.compaction": { contextWindowUsedBefore: 180000, contextWindowUsedAfter: 60000, strategy: "summary" } } } } as never)
          return { stopReason: "end_turn" }
        }
        else await self.emitBasicEvents(params.sessionId)
        return { stopReason: "end_turn" }
      },
      async cancel(params) {
        self.calls.push({ event: "cancel", ...params })
        if (self.scenario === "cancel-hangs" || self.cancelHangs) {
          await new Promise(() => {})
        }
      },
      async authenticate() { return {} },
    }
  }

  private async runLivenessPrompt(sessionId: string) {
    if (this.promptCount === 1) {
      return await new Promise<{ stopReason: "end_turn" }>((resolve) => { this.initialPromptResolve = resolve })
    }
    await this.connection.sessionUpdate(textUpdate(sessionId, "probe-alive"))
      setTimeout(async () => {
        await this.connection.sessionUpdate(textUpdate(sessionId, "done-after-probe"))
        this.initialPromptResolve?.({ stopReason: "end_turn" })
      }, 20)
    return { stopReason: "end_turn" as const }
  }

  private async runQuietThenDonePrompt(sessionId: string) {
    if (this.promptCount > 1) return { stopReason: "end_turn" as const }

    await new Promise<void>((resolve) => setTimeout(resolve, 120))
    await this.connection.sessionUpdate(textUpdate(sessionId, "done-after-quiet-period"))
    return { stopReason: "end_turn" as const }
  }

  private async runNonMessageLivenessPrompt(sessionId: string) {
    await new Promise<void>((resolve) => setTimeout(resolve, 20))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "agent_thought_chunk", content: { type: "text", text: "thinking" } } } as never)
    await new Promise<void>((resolve) => setTimeout(resolve, 20))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "tool-quiet", title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } } as never)
    await new Promise<void>((resolve) => setTimeout(resolve, 20))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "tool-quiet", title: "Read file", status: "completed", rawOutput: { text: "content" } } } as never)
    return { stopReason: "end_turn" as const }
  }

  private async runToolLivenessPrompt(sessionId: string) {
    if (this.promptCount === 1) {
      return await new Promise<{ stopReason: "end_turn" }>((resolve) => { this.initialPromptResolve = resolve })
    }
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "tool-probe-1", title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } })
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "tool-probe-1", title: "Read file", status: "completed", rawOutput: { text: "content" } } })
    setTimeout(() => {
      this.initialPromptResolve?.({ stopReason: "end_turn" })
    }, 20)
    return { stopReason: "end_turn" as const }
  }

  private async runProbeTimeoutPrompt() {
    return await new Promise<{ stopReason: "end_turn" }>(() => {})
  }

  private async runAbortDuringProbePrompt() {
    return await new Promise<{ stopReason: "end_turn" }>(() => {})
  }

  private async runExpectationRepairPrompt(sessionId: string, text: string) {
    if (text.includes("did not satisfy this task's completion requirements")) {
      await writeFile(join(this.extractCwd(), "review.md"), "<promise>PASS</promise>\n")
      await this.connection.sessionUpdate(textUpdate(sessionId, "wrote review.md"))
      return { stopReason: "end_turn" as const }
    }

    await this.connection.sessionUpdate(textUpdate(sessionId, "review complete"))
    return { stopReason: "end_turn" as const }
  }

  private async runExpectationRepairUsageOnlyPrompt(sessionId: string, text: string) {
    if (text.includes("did not satisfy this task's completion requirements")) {
      await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "usage_update", size: 262144, used: 0, cost: { amount: 0, currency: "USD" } } } as never)
      return { stopReason: "end_turn" as const }
    }

    await this.connection.sessionUpdate(textUpdate(sessionId, "review complete"))
    return { stopReason: "end_turn" as const }
  }

  private extractCwd() {
    const newSession = this.calls.findLast?.((entry) => entry.event === "newSession") ?? [...this.calls].reverse().find((entry) => entry.event === "newSession")
    return typeof newSession?.cwd === "string" ? newSession.cwd : tmpdir()
  }

  private async emitBasicEvents(sessionId: string) {
    await this.connection.sessionUpdate(textUpdate(sessionId, "hello"))
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "tool-1", title: "Read file", kind: "read", status: "in_progress", rawInput: { path: "README.md" } } })
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "tool-1", title: "Read file", status: "completed", rawOutput: { text: "content" } } })
  }

  private async emitWeirdToolEvents(sessionId: string) {
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call", toolCallId: "provider-tool-1", title: "Run bash command", status: "in_progress", rawInput: { command: "npm test" } } })
    await this.connection.sessionUpdate({ sessionId, update: { sessionUpdate: "tool_call_update", toolCallId: "provider-tool-1", title: "Run bash command", status: "completed", rawOutput: { stdout: "ok" } } })
  }
}

function textUpdate(sessionId: string, text: string) {
  return { sessionId, update: { sessionUpdate: "agent_message_chunk" as const, content: { type: "text" as const, text } } }
}

function thoughtUpdate(sessionId: string, text: string) {
  return { sessionId, update: { sessionUpdate: "agent_thought_chunk" as const, content: { type: "text" as const, text } } }
}

function createSharedSessionFixture(
  scenario: "thought-liveness" | "probe-send-failed",
  options?: {
    cachedModel?: string
    newSessionId?: string
    sessionRecord?: { acpSessionId: string; model?: string | null }
  },
) {
  const agent = new FakeSharedAcpAgent(scenario, { newSessionId: options?.newSessionId })
  const [clientStream, agentStream] = linkedStreams()
  const sessionUpdateHandlers = new Map<string, (notification: SessionNotification) => Promise<void>>()
  const permissionHandlers = new Map<string, (params: RequestPermissionRequest) => Promise<RequestPermissionResponse>>()
  const clientConnection = new ClientSideConnection(() => ({
    sessionUpdate: async (notification) => {
      await (sessionUpdateHandlers.get(notification.sessionId) ?? (async () => {}))(notification)
    },
    requestPermission: async (params) => await (permissionHandlers.get(params.sessionId) ?? (async () => ({ outcome: { outcome: "cancelled" } } as RequestPermissionResponse)))(params),
  }), clientStream)
  const agentConnection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(agentConnection)

  const serverConnection = new FakeServerConnection()
  const acpSessionManager = new AcpSessionManager()
  acpSessionManager.set(acpSessionManager.key("workflow-1", "shared-session"), { sessionId: "shared-session-1", workDir: "D:/fake/work", model: options?.cachedModel })
  serverConnection.nextEnsureWorkflowAgentSession = options?.sessionRecord ? { ...options.sessionRecord, workDir: "D:/fake/work" } : { acpSessionId: "shared-session-1", workDir: "D:/fake/work" }
  const connection = clientConnection
  if (scenario === "probe-send-failed") {
    const originalPrompt = clientConnection.prompt.bind(clientConnection)
    connection.prompt = async (params: Parameters<ClientSideConnection["prompt"]>[0]) => {
      const text = params.prompt.map((part) => part.type === "text" ? part.text : "").join("\n")
      if (text.includes("still alive")) throw new Error("probe transport failed")
      return await originalPrompt(params)
    }
  }
  const acpConnection: SharedAcpConnection = {
    connection,
    processPid: 4321,
    setSessionHandlers(sessionId, sessionUpdate, requestPermission) {
      sessionUpdateHandlers.set(sessionId, sessionUpdate)
      permissionHandlers.set(sessionId, requestPermission)
    },
    clearSessionHandlers(sessionId) {
      sessionUpdateHandlers.delete(sessionId)
      permissionHandlers.delete(sessionId)
    },
    async shutdown() {},
  }

  return {
    agent,
    serverConnection,
    context(): Partial<ActionContext> {
      return {
        acpConnection,
        acpSessionManager,
        serverConnection: serverConnection as unknown as ServerConnection,
      }
    },
  }
}

class FakeServerConnection {
  readonly calls: Array<{ event: string; type?: string; payload?: unknown; body?: unknown; sessionName?: string }> = []
  nextEnsureWorkflowAgentSession: { acpSessionId?: string; workDir?: string; model?: string | null } = { acpSessionId: "shared-session-1", workDir: "D:/fake/work" }

  constructor(private readonly timeline?: Array<{ event: string }>) {}

  async ensureWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string) {
    this.calls.push({ event: "ensureWorkflowAgentSession", sessionName })
    return this.nextEnsureWorkflowAgentSession
  }

  async getWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string) {
    this.calls.push({ event: "getWorkflowAgentSession", sessionName })
    return null
  }

  async openWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string, body: unknown) {
    this.calls.push({ event: "openWorkflowAgentSession", sessionName, body })
    return this.nextEnsureWorkflowAgentSession
  }

  async attachWorkflowAgentSession(_projectId: string, _workflowRunId: string, sessionName: string, body: unknown) {
    this.timeline?.push({ event: "attachWorkflowAgentSession" })
    this.calls.push({ event: "attachWorkflowAgentSession", sessionName, body })
  }

  async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, sessionName: string, payload: { runtimeEvents: Array<{ type: string; payload: unknown }> }) {
    for (const event of payload.runtimeEvents) this.calls.push({ event: "workflowAgentSessionEvents", sessionName, type: event.type, payload: event.payload })
  }

  async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, sessionName: string, payload: { events?: Array<{ type: string; payload: unknown }>; runtimeEvents?: Array<{ type: string; payload: unknown }> }) {
    const events = payload?.events ?? payload?.runtimeEvents ?? []
    for (const event of events) this.calls.push({ event: "workflowAgentSessionEvents", sessionName, type: event.type, payload: event.payload })
  }
}

class FakeSharedAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection

  constructor(
    private readonly scenario: "thought-liveness" | "probe-send-failed" | "resolved-model" | "compaction",
    private readonly options: { newSessionId?: string } = {},
  ) {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize() {
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-shared-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession(params) {
        self.calls.push({ event: "newSession", _meta: params._meta })
        return { sessionId: self.options.newSessionId ?? "shared-session-1" }
      },
        async resumeSession(params) {
          self.calls.push({ event: "resumeSession", sessionId: params.sessionId, cwd: params.cwd, _meta: params._meta })
          if (self.scenario === "resolved-model") {
            return { sessionId: params.sessionId, models: { currentModelId: "anthropic/claude-haiku-4-5" } }
          }
          return {}
        },
      async setSessionConfigOption(params) {
        self.calls.push({ event: "setSessionConfigOption", ...params })
        return { configOptions: [] }
      },
      async prompt(params) {
        self.calls.push({ event: "prompt", sessionId: params.sessionId, text: params.prompt.map((part) => part.type === "text" ? part.text : "").join("\n") })
        if (self.scenario === "thought-liveness") {
          for (let index = 0; index < 5; index += 1) {
            await delay(20)
            self.calls.push({ event: "thought", index })
            await self.connection.sessionUpdate(thoughtUpdate(params.sessionId, `thinking-${index}`))
          }
        } else if (self.scenario === "probe-send-failed") {
          await delay(80)
        } else if (self.scenario === "resolved-model") {
          await self.connection.sessionUpdate(thoughtUpdate(params.sessionId, "thinking"))
        }
        return { stopReason: "end_turn" }
      },
      async closeSession(params) {
        self.calls.push({ event: "closeSession", sessionId: params.sessionId })
      },
      async cancel() {
        self.calls.push({ event: "cancel" })
        if (self.cancelHangs) {
          await new Promise(() => {})
        }
      },
      async authenticate() { return {} },
    }
  }
}

function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function createTrackedFakeProcess(agent: FakeAcpAgent, options: { hangCancelWrites?: boolean } = {}): AcpProcessHandle & { cleanupCount: () => number } {
  const base = createFakeProcess(agent, options)
  let cleanupCalls = 0
  return {
    ...base,
    cleanupCount: () => cleanupCalls,
    async cleanup() {
      cleanupCalls += 1
      await base.cleanup()
    },
  }
}

function createFakeProcess(agent: FakeAcpAgent, options: { hangCancelWrites?: boolean } = {}): AcpProcessHandle {
  const [baseClientStream, agentStream] = linkedStreams()
  const clientStream: Stream = options.hangCancelWrites
    ? { writable: createCancelHangingWritable(baseClientStream.writable), readable: baseClientStream.readable }
    : baseClientStream
  const connection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(connection)
  return {
    stream: clientStream,
    processPid: 12345,
    spawnFailure: new Promise<never>(() => {}),
    exitFailure: new Promise<never>(() => {}),
    markInitialized() {},
    exitCode() { return 0 },
    async cleanup() {
      await Promise.allSettled([clientStream.readable.cancel(), clientStream.writable.abort()])
    },
  }
}

function createCancelHangingWritable(inner: WritableStream<any>): WritableStream<any> {
  let pendingWriteReject: ((reason: unknown) => void) | undefined
  const stream = new WritableStream<any>({
    async write(chunk) {
      const text = describeMessage(chunk)
      if (text.includes("\"session/cancel\"")) {
        await new Promise<void>((_, reject) => { pendingWriteReject = reject })
        return
      }
      const writer = inner.getWriter()
      try {
        await writer.write(chunk)
      } finally {
        writer.releaseLock()
      }
    },
    async abort(reason) {
      pendingWriteReject?.(reason)
      pendingWriteReject = undefined
      try {
        await inner.abort(reason)
      } catch {}
    },
    async close() {
      pendingWriteReject?.(new Error("stream closed"))
      pendingWriteReject = undefined
      try {
        await inner.close()
      } catch {}
    },
  })
  return stream
}

function describeMessage(message: unknown): string {
  if (typeof message === "string") return message
  if (message instanceof Uint8Array) return new TextDecoder().decode(message)
  try {
    return JSON.stringify(message)
  } catch {
    return String(message)
  }
}

function createSharedConnection(stream: Stream): SharedAcpConnection {
  const sessionUpdateHandlers = new Map<string, Parameters<SharedAcpConnection["setSessionHandlers"]>[1]>()
  const permissionHandlers = new Map<string, Parameters<SharedAcpConnection["setSessionHandlers"]>[2]>()
  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification) => {
        await (sessionUpdateHandlers.get(notification.sessionId) ?? (async () => {}))(notification)
      },
      requestPermission: async (params) => (permissionHandlers.get(params.sessionId) ?? (async () => ({ outcome: { outcome: "cancelled" } } as RequestPermissionResponse)))(params),
    }),
    stream,
  )

  return {
    connection,
    processPid: 12345,
    setSessionHandlers(sessionId, sessionUpdate, permission) {
      sessionUpdateHandlers.set(sessionId, sessionUpdate)
      permissionHandlers.set(sessionId, permission)
    },
    clearSessionHandlers(sessionId) {
      sessionUpdateHandlers.delete(sessionId)
      permissionHandlers.delete(sessionId)
    },
    async shutdown() {
      await Promise.allSettled([stream.readable.cancel(), stream.writable.abort()])
    },
  }
}

function fakeServerConnection() {
  const events: Array<{ type: string; payload: unknown }> = []
  return {
    events,
    async ensureWorkflowAgentSession() {
      return {}
    },
    async getWorkflowAgentSession() {
      return null
    },
    async openWorkflowAgentSession() {
      return {}
    },
    async attachWorkflowAgentSession() {},
    async workflowAgentSessionEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: { events?: Array<{ type: string; payload: unknown }> }) {
      events.push(...(body.events ?? []))
    },
    async workflowAgentSessionRuntimeEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: { events?: Array<{ type: string; payload: unknown }>; runtimeEvents?: Array<{ type: string; payload: unknown }> }) {
      const all = body?.events ?? body?.runtimeEvents ?? []
      events.push(...all)
    },
  }
}

function linkedStreams(): [Stream, Stream] {
  const clientToAgent = new TransformStream()
  const agentToClient = new TransformStream()
  return [
    { writable: clientToAgent.writable, readable: agentToClient.readable },
    { writable: agentToClient.writable, readable: clientToAgent.readable },
  ]
}
