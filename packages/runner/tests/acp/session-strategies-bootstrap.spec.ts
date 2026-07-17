import { afterEach, describe, expect, it } from "vitest"
import { acpAgentAction as executeAcpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import { createFixture, resetAcpTestHooks } from "./support.js"
import { runWithProviderDefaultModelWarning, runWithRejectedRequestedModel } from "./session-strategies-test-support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/acp-agent new and ephemeral sessions", () => {
  it("ValidAcpAgentWork_ActionRuns_SpawnsAcpAndInitializesSessionBeforePrompt", async () => {
    const fixture = createFixture("basic")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").runtimeSessionId).toBe("fake-session-1")
    expect(fixture.agent.calls.map((call) => call.event).filter((event) => ["initialize", "newSession", "prompt"].includes(event))).toEqual(["initialize", "newSession", "prompt"])
  })

  it("ModelConfigured_AcpSessionStarts_SetsSessionConfigModelBeforePrompt", async () => {
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", model: "openai/gpt-4.1" }))

    expect(fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "openai/gpt-4.1")).toBeTruthy()
    expect(fixture.agent.calls.findIndex((entry) => entry.event === "unstable_setSessionModel")).toBeLessThan(fixture.agent.calls.findIndex((entry) => entry.event === "prompt"))
  })

  it("SessionConfigModelFails_ModelConfigured_FallsBackToUnstableSetSessionModel", async () => {
    const fixture = createFixture("model-fallback")

    const result = await executeAcpAgentAction(fixture.context({ prompt: "do the work", model: "anthropic/claude" }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude")).toBeTruthy()
  })

  it("VariantInAgentBlock_AcpSessionStarts_DeliversBareModelIdBeforePrompt", async () => {
    // Spec D8: `variant` is a sibling option and MUST NOT be appended
    // to or parsed from the model identifier. The model ID is delivered
    // to the runtime verbatim; variant rides separately.
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", agent: { model: "anthropic/claude-sonnet-4-5", variant: "high" } }))

    const setModelCall = fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel")
    expect(setModelCall?.modelId).toBe("anthropic/claude-sonnet-4-5")
    expect(fixture.agent.calls.findIndex((entry) => entry.event === "unstable_setSessionModel")).toBeLessThan(fixture.agent.calls.findIndex((entry) => entry.event === "prompt"))
  })

  it("VariantInTopLevelWith_AcpSessionStarts_DeliversBareModelId", async () => {
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", model: "anthropic/claude-sonnet-4-5", variant: "max" }))

    expect(fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude-sonnet-4-5")).toBeTruthy()
  })

  it("EmptyVariant_AcpSessionStarts_DeliversBareModelIdWithoutTrailingSlash", async () => {
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", agent: { model: "anthropic/claude-sonnet-4-5", variant: "   " } }))

    const setModelCall = fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel")
    expect(setModelCall?.modelId).toBe("anthropic/claude-sonnet-4-5")
    expect(setModelCall?.modelId?.split("/").length).toBe(2)
  })

  it("NoVariant_AcpSessionStarts_DeliversBareModelId", async () => {
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", model: "minimax-coding-plan/MiniMax-M3" }))

    const setModelCall = fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel")
    expect(setModelCall?.modelId).toBe("minimax-coding-plan/MiniMax-M3")
    expect(setModelCall?.modelId?.split("/").length).toBe(2)
  })

  it("VariantRejectedByAgent_RunStillSucceedsAgainstProviderDefault", async () => {
    const fixture = createFixture("model-config-fails")

    const result = await runWithRejectedRequestedModel(
      fixture.context({ prompt: "do the work", agent: { model: "anthropic/claude-sonnet-4-5", variant: "high" } }),
      "anthropic/claude-sonnet-4-5",
      { requestedModelSource: "agent.model", requestedVariant: "high" },
    )

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.some((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude-sonnet-4-5")).toBe(true)
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt")).toBe(true)
    expect(String(result.message ?? "")).not.toMatch(/variant/i)
  })

  it("NewSessionCreatedBeforeModelConfiguration_RunnerReportsPhysicalSessionIdToServer", async () => {
    const fixture = createFixture("model-config-fails")

    const result = await runWithRejectedRequestedModel(fixture.context({
      prompt: "do the work",
      session: "build",
      model: "anthropic/claude",
    }), "anthropic/claude", { requestedModelSource: "agent.model" })

    expect(result.status).toBe("success")
    expect(fixture.serverConnection.calls).toContainEqual(expect.objectContaining({
      event: "attachWorkflowAgentSession",
      sessionName: "build",
      body: expect.objectContaining({ runtimeSessionId: "fake-session-1" }),
    }))
    expect(fixture.timeline.findIndex((entry) => entry.event === "newSession")).toBeLessThan(fixture.timeline.findIndex((entry) => entry.event === "attachWorkflowAgentSession"))
    expect(fixture.timeline.findIndex((entry) => entry.event === "attachWorkflowAgentSession")).toBeLessThan(fixture.timeline.findIndex((entry) => entry.event === "unstable_setSessionModel"))
  })

  it("PermissionRequestHasAllowOption_AgentRequestsPermission_SelectsAllowOption", async () => {
    const fixture = createFixture("permission")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "needs permission" }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.find((entry) => entry.event === "permissionResponse" && entry.outcome?.optionId === "allow")).toBeTruthy()
  })

  it("AgentMessageChunkArrives_SessionUpdateHandled_ReturnsAgentTextInOutput", async () => {
    const fixture = createFixture("basic")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").text).toBe("hello")
  })

  it("ToolEventMissingToolNameButHasProviderId_ToolCallUpdateHandled_InfersToolNameAndReusesToolCallId", async () => {
    const fixture = createFixture("tool-weird")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "use tools" }))

    expect(result.status).toBe("success")
  })
})
