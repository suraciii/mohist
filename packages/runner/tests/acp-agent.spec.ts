import { afterEach, describe, expect, it, vi } from "vitest"
import { AgentSideConnection, ClientSideConnection, PROTOCOL_VERSION } from "@agentclientprotocol/sdk"
import type { Agent, RequestPermissionRequest, RequestPermissionResponse, SessionNotification, Stream } from "@agentclientprotocol/sdk"
import { acpAgentAction, buildPromptWithMohistContext, setAcpProcessFactoryForTest, type AcpProcessHandle } from "../src/actions/acp-agent.js"
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
})

describe("mohist/acp-agent", () => {
  it("ValidAcpAgentWork_ActionRuns_SpawnsAcpAndInitializesSessionBeforePrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").acpSessionId).toBe("fake-session-1")
    expect(fixture.agent.calls.map((call) => call.event).filter((event) => ["initialize", "newSession", "prompt"].includes(event))).toEqual(["initialize", "newSession", "prompt"])
  })

  it("OpenSpecTaskWithoutPrompt_ActionBuildsPromptFromTaskFields", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      description: "Requeue runnable workflows on server startup.",
      acceptanceCriteria: ["runner can claim recovered work"],
      output: "packages/server/src/Mohist.Server/Workflow/Recovery",
    }))

    expect(result.status).toBe("success")
    const prompt = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(prompt).toContain("Implement this task: Build task")
    expect(prompt).toContain("Requeue runnable workflows on server startup.")
    expect(prompt).toContain("runner can claim recovered work")
  })

  it("IssueVariablesPresent_ActionPrependsIssueContextToPrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({ prompt: "create the proposal" }))

    expect(result.status).toBe("success")
    const prompt = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(prompt).toContain("## Mohist Issue Context")
    expect(prompt).toContain("Number: 7")
    expect(prompt).toContain("Title: Document update smoke validation note")
    expect(prompt).toContain("Body:\nAdd a short note that records the expected local post-update smoke validation path.")
    expect(prompt).toContain("## Task Prompt\n\ncreate the proposal")
  })

  it("IssueVariablesMissing_PromptContextBuilderLeavesPromptUnchanged", () => {
    expect(buildPromptWithMohistContext({ variables: {}, issueNumber: null }, "plain prompt")).toBe("plain prompt")
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
      expect.arrayContaining(["agent_thought_chunk", "tool_call", "tool_call_update", "agent_session_terminal"]),
    )
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
    expect(shared.serverConnection.calls.some((entry) => entry.event === "ensureWorkflowAgentSession")).toBe(true)
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession")).toBe(false)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_thought_chunk")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_liveness_status" && (entry.payload as { status?: string }).status === "failed")).toBe(false)
    expect(result.message ?? "").not.toContain("Session liveness probe timed out")
    expect(JSON.parse(result.output ?? "{}").text).toBe("")
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
    expect(shared.serverConnection.calls.some((entry) => entry.event === "ensureWorkflowAgentSession")).toBe(true)
    expect(shared.agent.calls.some((entry) => entry.event === "resumeSession" && entry.sessionId === "server-session-1")).toBe(true)
    expect(shared.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    expect(shared.agent.calls.some((entry) => entry.event === "thought")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_thought_chunk")).toBe(true)
    expect(shared.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_liveness_status" && (entry.payload as { status?: string }).status === "failed")).toBe(false)
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
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_liveness_status")
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

    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "tool_call")).toBe(true)
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "tool_call_update")).toBe(true)
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
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_liveness_status")
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

    const probeState = JSON.parse((result.message ?? "").slice((result.message ?? "").indexOf("{"))) as Record<string, unknown>
    expect(probeState.probeSentAt).toBe(probing?.probeSentAt)
    expect(probeState.probeDeadlineAt).toBe(probing?.probeDeadlineAt)
    expect(probeState.probeVersion).toBe(probing?.activeProbeVersion)
    expect(probeState.postProbeActivity).toBe(false)
    expect(Number(probeState.dataVersion)).toBe(Number(probeState.probeVersion))
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
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_liveness_status")
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
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "agent_liveness_status" && (entry.payload as { failureReason?: string }).failureReason === "probe_timeout")).toBe(false)
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

  it("StringPrompt_ActionSendsPromptVerbatimBeforeMohistContextWrapper", async () => {
    const fixture = createFixture("basic")

    const literal = "Fix the build-stage health failure reported by `git diff --check`."
    const result = await acpAgentAction(fixture.context({ prompt: literal }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toContain("## Task Prompt")
    const taskPromptSection = sentText.slice(sentText.indexOf("## Task Prompt") + "## Task Prompt".length).trim()
    expect(taskPromptSection).toBe(literal)
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

  it("ObjectPrompt_ActionRendersThroughDefaultRendererBeforeMohistContextWrapper", async () => {
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
    expect(sentText).toContain("## Task Prompt")
    const taskPromptSection = sentText.slice(sentText.indexOf("## Task Prompt") + "## Task Prompt".length).trim()
    expect(taskPromptSection).toBe([
      `<artifact id="build-task">`,
      ``,
      `  <task>Complete exactly one implementation task.</task>`,
      ``,
      `  <instruction>Follow acceptance criteria.</instruction>`,
      ``,
      `</artifact>`,
    ].join("\n"))
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
    const taskPromptSection = sentText.slice(sentText.indexOf("## Task Prompt") + "## Task Prompt".length).trim()
    expect(taskPromptSection).toBe("loader produced task prompt")
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
    const taskPromptSection = sentText.slice(sentText.indexOf("## Task Prompt") + "## Task Prompt".length).trim()
    expect(taskPromptSection).toBe([
      `<artifact>`,
      ``,
      `  <task>rendered from loader</task>`,
      ``,
      `</artifact>`,
    ].join("\n"))
  })

  it("UsesFormPrompt_LoaderReceivesContextWithWorkflowVariablesWorkDirWorkIdTitleStageAndIssueNumber", async () => {
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
      issueNumber: 59,
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0] as PromptLoaderContext
    expect(received.with).toEqual({ file: "tasks.json", taskId: "T-001" })
    expect(received.variables).toEqual(variables)
    expect(received.workDir).toBe("D:/fake/work")
    expect(received.workId).toBe("work-1")
    expect(received.title).toBe("Build task")
    expect(received.stage).toBe("build")
    expect(received.issueNumber).toBe(59)
  })

  it("UsesFormPrompt_LoaderReceivesContextWithNullTitleStageAndIssueNumberWhenAbsent", async () => {
    const fixture = createFixture("basic")
    const loader = vi.fn<PromptLoader>(async () => "ok")
    const registry = new PromptLoaderRegistry()
    registry.register("fake/echo-loader", loader)
    setPromptLoaderRegistryForTest(registry)

    await acpAgentAction(fixture.context({ prompt: { uses: "fake/echo-loader" } }, new AbortController().signal, {
      title: null,
      stage: null,
      issueNumber: null,
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0] as PromptLoaderContext
    expect(received.title).toBeNull()
    expect(received.stage).toBeNull()
    expect(received.issueNumber).toBeNull()
  })

  it("MissingPrompt_ActionStillUsesLegacyFallbackPrompt", async () => {
    const fixture = createFixture("basic")

    const result = await acpAgentAction(fixture.context({
      description: "Requeue runnable workflows on server startup.",
      acceptanceCriteria: ["runner can claim recovered work"],
    }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toContain("Implement this task: Build task")
    expect(sentText).toContain("Requeue runnable workflows on server startup.")
    expect(sentText).toContain("runner can claim recovered work")
  })

  it("UnknownPromptLoader_ActionFailsWithClearErrorBeforeAnyAcpInteraction", async () => {
    const fixture = createFixture("basic")
    setPromptLoaderRegistryForTest(new PromptLoaderRegistry())

    const result = await acpAgentAction(fixture.context({ prompt: { uses: "no/such-loader" } }))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Unknown prompt loader: 'no/such-loader'")
    expect(fixture.agent.calls.find((entry) => entry.event === "initialize")).toBeUndefined()
  })
})

function createFixture(scenario: Scenario) {
  const agent = new FakeAcpAgent(scenario)
  const serverConnection = new FakeServerConnection()
  setAcpProcessFactoryForTest(() => createFakeProcess(agent))
  return {
    agent,
    serverConnection,
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

type Scenario = "basic" | "model-fallback" | "permission" | "tool-weird" | "liveness" | "quiet-then-done" | "liveness-non-message" | "abort" | "tool-liveness" | "probe-timeout" | "abort-during-probe" | "empty-complete"

class FakeAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection
  private promptCount = 0
  private initialPromptResolve: ((value: { stopReason: "end_turn" }) => void) | null = null

  constructor(private readonly scenario: Scenario) {}

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
        self.calls.push({ event: "newSession", cwd: params.cwd })
        return { sessionId: "fake-session-1" }
      },
      async setSessionConfigOption(params) {
        self.calls.push({ event: "setSessionConfigOption", ...params })
        if (self.scenario === "model-fallback") throw new Error("set config unsupported")
        return { configOptions: [] }
      },
      async unstable_setSessionModel(params) {
        self.calls.push({ event: "unstable_setSessionModel", ...params })
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
        if (self.scenario === "empty-complete") return { stopReason: "end_turn" }
        if (self.scenario === "tool-weird") await self.emitWeirdToolEvents(params.sessionId)
        else await self.emitBasicEvents(params.sessionId)
        return { stopReason: "end_turn" }
      },
      async cancel(params) {
        self.calls.push({ event: "cancel", ...params })
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

function createSharedSessionFixture(scenario: "thought-liveness" | "probe-send-failed", options?: { sessionRecord?: { acpSessionId: string } }) {
  const agent = new FakeSharedAcpAgent(scenario)
  const [clientStream, agentStream] = linkedStreams()
  let activeSessionUpdateHandler: (notification: SessionNotification) => Promise<void> = async () => {}
  let activePermissionHandler: (params: RequestPermissionRequest) => Promise<RequestPermissionResponse> = async () => ({ outcome: { outcome: "cancelled" } })
  const clientConnection = new ClientSideConnection(() => ({
    sessionUpdate: async (notification) => {
      await activeSessionUpdateHandler(notification)
    },
    requestPermission: async (params) => await activePermissionHandler(params),
  }), clientStream)
  const agentConnection = new AgentSideConnection(() => agent.handler(), agentStream)
  agent.bind(agentConnection)

  const serverConnection = new FakeServerConnection()
  const acpSessionManager = new AcpSessionManager()
  acpSessionManager.set(acpSessionManager.key("workflow-1", "shared-session"), { sessionId: "shared-session-1", workDir: "D:/fake/work" })
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
    setActiveHandlers(sessionUpdate, requestPermission) {
      activeSessionUpdateHandler = sessionUpdate
      activePermissionHandler = requestPermission
    },
    clearActiveHandlers() {
      activeSessionUpdateHandler = async () => {}
      activePermissionHandler = async () => ({ outcome: { outcome: "cancelled" } })
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
  readonly calls: Array<{ event: string; type?: string; payload?: unknown; args?: unknown[] }> = []
  nextEnsureWorkflowAgentSession: { acpSessionId?: string; workDir?: string } = { acpSessionId: "shared-session-1", workDir: "D:/fake/work" }

  async ensureWorkflowAgentSession() {
    this.calls.push({ event: "ensureWorkflowAgentSession" })
    return this.nextEnsureWorkflowAgentSession
  }

  async attachWorkflowAgentSession(...args: unknown[]) {
    this.calls.push({ event: "attachWorkflowAgentSession", args })
  }

  async workflowAgentSessionEvents(_projectId: string, _workflowRunId: string, _sessionName: string, payload: { events: Array<{ type: string; payload: unknown }> }) {
    for (const event of payload.events) this.calls.push({ event: "workflowAgentSessionEvents", type: event.type, payload: event.payload })
  }
}

class FakeSharedAcpAgent {
  readonly calls: any[] = []
  private connection!: AgentSideConnection

  constructor(private readonly scenario: "thought-liveness" | "probe-send-failed") {}

  bind(connection: AgentSideConnection) {
    this.connection = connection
  }

  handler(): Agent {
    const self = this
    return {
      async initialize() {
        return { protocolVersion: PROTOCOL_VERSION, agentInfo: { name: "fake-shared-acp-agent", version: "0.1.0" }, agentCapabilities: {} }
      },
      async newSession() {
        self.calls.push({ event: "newSession" })
        return { sessionId: "shared-session-1" }
      },
        async resumeSession(params) {
          self.calls.push({ event: "resumeSession", sessionId: params.sessionId, cwd: params.cwd })
          return {}
        },
      async prompt(params) {
        self.calls.push({ event: "prompt", text: params.prompt.map((part) => part.type === "text" ? part.text : "").join("\n") })
        if (self.scenario === "thought-liveness") {
          for (let index = 0; index < 5; index += 1) {
            await delay(20)
            self.calls.push({ event: "thought", index })
            await self.connection.sessionUpdate(thoughtUpdate(params.sessionId, `thinking-${index}`))
          }
        } else if (self.scenario === "probe-send-failed") {
          await delay(80)
        }
        return { stopReason: "end_turn" }
      },
      async cancel() {},
      async authenticate() { return {} },
    }
  }
}

function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function createFakeProcess(agent: FakeAcpAgent): AcpProcessHandle {
  const [clientStream, agentStream] = linkedStreams()
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

function createSharedConnection(stream: Stream): SharedAcpConnection {
  let activeSessionUpdateHandler: Parameters<SharedAcpConnection["setActiveHandlers"]>[0] = async () => {}
  let activePermissionHandler: Parameters<SharedAcpConnection["setActiveHandlers"]>[1] = async () => ({ outcome: { outcome: "cancelled" } })
  const connection = new ClientSideConnection(
    () => ({
      sessionUpdate: async (notification) => {
        await activeSessionUpdateHandler(notification)
      },
      requestPermission: async (params) => activePermissionHandler(params),
    }),
    stream,
  )

  return {
    connection,
    processPid: 12345,
    setActiveHandlers(sessionUpdate, permission) {
      activeSessionUpdateHandler = sessionUpdate
      activePermissionHandler = permission
    },
    clearActiveHandlers() {
      activeSessionUpdateHandler = async () => {}
      activePermissionHandler = async () => ({ outcome: { outcome: "cancelled" } })
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
    async attachWorkflowAgentSession() {},
    async workflowAgentSessionEvents(_projectId: string, _workflowRunId: string, _sessionName: string, body: { events?: Array<{ type: string; payload: unknown }> }) {
      events.push(...(body.events ?? []))
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
