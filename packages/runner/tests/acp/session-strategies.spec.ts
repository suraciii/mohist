import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import {
  PromptLoaderRegistry,
  setPromptLoaderRegistryForTest,
  type PromptLoader,
  type PromptLoaderContext,
} from "../../src/core/prompt.js"
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