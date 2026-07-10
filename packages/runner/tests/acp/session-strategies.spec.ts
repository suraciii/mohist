import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it, vi } from "vitest"
import { acpAgentAction as executeAcpAgentAction, setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import {
  PromptLoaderRegistry,
  setPromptLoaderRegistryForTest,
  type PromptLoader,
} from "../../src/core/prompt.js"
import { stringInput } from "../../src/core/json.js"
import {
  contextWithOverrides,
  createFixture,
  createSharedFixture,
  createSharedSessionFixture,
  resetAcpTestHooks,
  useAcpFakeTimers,
} from "./support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

type AcpActionResult = Awaited<ReturnType<typeof executeAcpAgentAction>>

async function runWithProviderDefaultModelWarning(
  context: Parameters<typeof executeAcpAgentAction>[0],
  drive?: (action: ReturnType<typeof executeAcpAgentAction>) => Promise<AcpActionResult>,
) {
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const action = executeAcpAgentAction(context)
    const result = drive === undefined ? await action : await drive(action)

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

async function runWithRejectedRequestedModel(
  context: Parameters<typeof executeAcpAgentAction>[0],
  requestedModel: string,
  expected: { requestedModelSource: "agent.model"; requestedVariant?: string },
) {
  const errorSpy = vi.spyOn(console, "error").mockClear().mockImplementation(() => undefined)
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const result = await executeAcpAgentAction(context)

    expect(errorSpy).toHaveBeenCalledTimes(1)
    expect(errorSpy).toHaveBeenNthCalledWith(
      1,
      "Error handling request",
      expect.objectContaining({ method: "session/set_model", params: expect.objectContaining({ modelId: requestedModel }) }),
      expect.objectContaining({ code: -32603, message: "Internal error" }),
    )
    expect(warningSpy).toHaveBeenCalledTimes(1)
    expect(warningSpy).toHaveBeenNthCalledWith(
      1,
      "mohist acp set requested model failed; provider default may be used",
      {
        ...providerDefaultModelWarningContext(context),
        requestedModel,
        requestedModelSource: expected.requestedModelSource,
        ...(expected.requestedVariant === undefined ? {} : { requestedVariant: expected.requestedVariant }),
        variantDelivered: false,
        error: "Internal error",
      },
    )
    return result
  } finally {
    warningSpy.mockRestore()
    errorSpy.mockRestore()
  }
}

function providerDefaultModelWarningContext(context: Parameters<typeof executeAcpAgentAction>[0]) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: stringInput(context.with, "session") ?? context.workId,
    requestedModel: null,
    requestedModelSource: "none",
  }
}

describe("mohist/acp-agent new and ephemeral sessions", () => {
  it("ValidAcpAgentWork_ActionRuns_SpawnsAcpAndInitializesSessionBeforePrompt", async () => {
    const fixture = createFixture("basic")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(JSON.parse(result.output ?? "{}").acpSessionId).toBe("fake-session-1")
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

  it("VariantInAgentBlock_AcpSessionStarts_DeliversComposedSlashModelIdBeforePrompt", async () => {
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", agent: { model: "anthropic/claude-sonnet-4-5", variant: "high" } }))

    const setModelCall = fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel")
    expect(setModelCall?.modelId).toBe("anthropic/claude-sonnet-4-5/high")
    expect(fixture.agent.calls.findIndex((entry) => entry.event === "unstable_setSessionModel")).toBeLessThan(fixture.agent.calls.findIndex((entry) => entry.event === "prompt"))
  })

  it("VariantInTopLevelWith_AcpSessionStarts_DeliversComposedSlashModelId", async () => {
    const fixture = createFixture("basic")

    await executeAcpAgentAction(fixture.context({ prompt: "do the work", model: "anthropic/claude-sonnet-4-5", variant: "max" }))

    expect(fixture.agent.calls.find((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude-sonnet-4-5/max")).toBeTruthy()
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
      "anthropic/claude-sonnet-4-5/high",
      { requestedModelSource: "agent.model", requestedVariant: "high" },
    )

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.some((entry) => entry.event === "unstable_setSessionModel" && entry.modelId === "anthropic/claude-sonnet-4-5/high")).toBe(true)
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
      body: expect.objectContaining({ agentSessionId: "fake-session-1" }),
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

  it("RunningSessionExceedsQuietThreshold_LivenessMonitored_EntersProbingAndSendsProbePrompt", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("liveness")

    const result = await runWithProviderDefaultModelWarning(
      fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }),
      async (action) => {
        await fixture.agent.waitForPrompt()
        await vi.advanceTimersByTimeAsync(30)
        await vi.advanceTimersByTimeAsync(20)
        return action
      },
    )

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt" && entry.promptCount === 2 && entry.text.includes("still alive"))).toBe(true)
  })

  it("PromptCompletesWithoutSessionActivity_ActionFailsInsteadOfReportingEmptySuccess", async () => {
    const fixture = createFixture("empty-complete")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("failure")
    expect(result.message).toContain("without any prompt work activity")
  })

  it("PromptCompletesWithUsageOnly_ActionFailsInsteadOfReportingEmptySuccess", async () => {
    const fixture = createFixture("usage-only")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("failure")
    expect(result.message).toContain("without any prompt work activity")
  })

  it("ExpectedArtifactMissing_AgentIsAskedToRepairArtifactBeforeTaskFails", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-acp-expect-"))
    const fixture = createFixture("expectation-repair")

    try {
      const result = await runWithProviderDefaultModelWarning(fixture.context({
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
      const result = await runWithProviderDefaultModelWarning(fixture.context({
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
      const result = await runWithProviderDefaultModelWarning(fixture.context({
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
      expect(result.message).toContain("without any prompt work activity")
      expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(2)
    } finally {
      await rm(workDir, { recursive: true, force: true })
    }
  })

  it("FailIf_PASSMarker_ActionReportsSuccess", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-acp-failif-pass-"))
    const fixture = createFixture("expectation-repair")

    try {
      const result = await runWithProviderDefaultModelWarning(fixture.context({
        prompt: "review the change",
        session: "check",
        expect: {
          markers: [
            {
              path: "review.md",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
              failIf: "<promise>FAIL</promise>",
            },
          ],
        },
      }, undefined, { workDir }))

      expect(result.status).toBe("success")
      const output = JSON.parse(result.output ?? "{}")
      expect(output.promise).toBe("PASS")
      expect(output.failIfMarker).toBeNull()
      expect(output.expectation.satisfied).toBe(true)
      expect(output.expectation.failIfMatches).toHaveLength(0)
    } finally {
      await rm(workDir, { recursive: true, force: true })
    }
  })

  it("FailIf_FAILMarker_ActionReportsFailureWithFailPromise", async () => {
    const workDir = await mkdtemp(join(tmpdir(), "mohist-acp-failif-fail-"))
    const fixture = createFixture("failif-fail")

    try {
      const result = await runWithProviderDefaultModelWarning(fixture.context({
        prompt: "review the change",
        session: "check",
        expect: {
          markers: [
            {
              path: "review.md",
              oneOf: ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
              failIf: "<promise>FAIL</promise>",
            },
          ],
        },
      }, undefined, { workDir }))

      expect(result.status).toBe("failure")
      const output = JSON.parse(result.output ?? "{}")
      expect(output.promise).toBe("FAIL")
      expect(output.failIfMarker).toBe("<promise>FAIL</promise>")
      expect(output.expectation.satisfied).toBe(false)
      expect(output.expectation.failIfMatches).toHaveLength(1)
      expect(result.message).toContain("failIf marker matched")
      expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    } finally {
      await rm(workDir, { recursive: true, force: true })
    }
  })

  it("ProbeTimesOutWithoutQualifyingActivity_LivenessMonitored_FailsSession", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("quiet-then-done")

    const result = await runWithProviderDefaultModelWarning(
      fixture.context({ prompt: "long silent task", livenessQuietThresholdMs: 30, probeTimeoutMs: 30, timeout: 2_000 }),
      async (action) => {
        await fixture.agent.waitForPrompt()
        await vi.advanceTimersByTimeAsync(30)
        await vi.advanceTimersByTimeAsync(30)
        return action
      },
    )

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Session liveness probe timed out")
    expect(fixture.agent.calls.some((entry) => entry.event === "prompt" && entry.promptCount === 2 && entry.text.includes("still alive"))).toBe(true)
  })

  it("ThoughtAndToolUpdatesArrive_LivenessMonitored_DoNotProbeWhileAgentIsActive", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("liveness-non-message")

    const result = await runWithProviderDefaultModelWarning(
      fixture.context({ prompt: "long task", livenessQuietThresholdMs: 30, probeTimeoutMs: 500, timeout: 2_000 }),
      async (action) => {
        await fixture.agent.waitForPrompt()
        await vi.advanceTimersByTimeAsync(60)
        return action
      },
    )

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
  })

  it("AbortSignalFires_PromptRunning_SendsSessionCancelBeforeCleanup", async () => {
    useAcpFakeTimers()
    const fixture = createFixture("abort")
    const controller = new AbortController()

    const action = runWithProviderDefaultModelWarning(fixture.context({ prompt: "cancel me", timeout: 500 }, controller.signal))
    await fixture.agent.waitForPrompt()
    controller.abort()
    const result = await action

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toMatch(/stopped by user/i)
    expect(fixture.agent.calls.some((entry) => entry.event === "cancel")).toBe(true)
  })

  it("StringPrompt_ActionSendsPromptVerbatimWithoutMarkdownEnvelope", async () => {
    const fixture = createFixture("basic")

    const literal = "Fix the build-stage health failure reported by `git diff --check`.\n\n## Keep this markdown verbatim"
    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: literal }))

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
    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: literal }, undefined, {
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
    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: literal }))

    expect(result.status).toBe("success")
    const sentText = fixture.agent.calls.find((entry) => entry.event === "prompt")?.text ?? ""
    expect(sentText).toContain(literal)
    expect(sentText).not.toContain("prompts.xxx".replace("xxx", "build"))
  })

  it("ObjectPrompt_ActionSendsRenderedXmlWithoutMarkdownEnvelope", async () => {
    const fixture = createFixture("basic")

    const result = await runWithProviderDefaultModelWarning(fixture.context({
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

    const result = await runWithProviderDefaultModelWarning(fixture.context({
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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: { uses: "fake/object-loader" } }))

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
    await runWithProviderDefaultModelWarning(fixture.context({
      prompt: { uses: "fake/echo-loader", with: { file: "tasks.json", taskId: "T-001" } },
    }, new AbortController().signal, {
      variables: variables as never,
      stage: "build",
      title: "Build task",
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0]
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

    await runWithProviderDefaultModelWarning(fixture.context({ prompt: { uses: "fake/echo-loader" } }, new AbortController().signal, {
      title: null,
      stage: null,
    }))

    expect(loader).toHaveBeenCalledTimes(1)
    const received = loader.mock.calls[0][0]
    expect(received.title).toBeNull()
    expect(received.stage).toBeNull()
  })

  it("MissingPrompt_ActionFailsWithoutSendingSynthesizedPrompt", async () => {
    const fixture = createFixture("basic")

    const result = await executeAcpAgentAction(fixture.context({
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

    const result = await executeAcpAgentAction(fixture.context({ prompt: { uses: "no/such-loader" } }))

    expect(result.status).toBe("failure")
    expect(result.message ?? "").toContain("Unknown prompt loader: 'no/such-loader'")
    expect(fixture.agent.calls.find((entry) => entry.event === "initialize")).toBeUndefined()
  })

  it("NewSessionReturnsCurrentModelId_RunnerEmitsResolvedModelEvent", async () => {
    const fixture = createFixture("resolved-model")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

    expect(result.status).toBe("success")
    expect(fixture.serverConnection.calls.some((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "model.resolved")).toBe(false)
  })

  it("ConfigOptionUpdateChangesModel_RunnerEmitsResolvedModelEvent", async () => {
    const fixture = createFixture("config-option-update")

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "switch the model" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "track usage" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "report usage" }))

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
    useAcpFakeTimers()
    const fixture = createFixture("probe-timeout")

    const result = await runWithProviderDefaultModelWarning(fixture.context({
      prompt: "quiet task",
      session: "timeout-session",
      livenessQuietThresholdMs: 30,
      probeTimeoutMs: 60,
      timeout: 1_000,
    }), async (action) => {
      await fixture.agent.waitForPrompt()
      await vi.advanceTimersByTimeAsync(30)
      await vi.advanceTimersByTimeAsync(60)
      return action
    })

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "happy path" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "do the work" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({
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

    const result = await runWithProviderDefaultModelWarning(fixture.context({
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

    const result = await runWithProviderDefaultModelWarning(fixture.context({
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

    const result = await runWithProviderDefaultModelWarning(fixture.context({
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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "trigger compaction" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "trigger compaction" }))

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

    const result = await runWithProviderDefaultModelWarning(fixture.context({ prompt: "trigger compaction" }))

    expect(result.status).toBe("success")
    const usageEvents = fixture.serverConnection.calls
      .filter((entry) => entry.event === "workflowAgentSessionEvents" && entry.type === "usage.updated")
      .map((entry) => entry.payload as Record<string, unknown>)
    const finalUsage = usageEvents[usageEvents.length - 1]
    expect(finalUsage?.contextWindowSize).toBe(200000)
    expect(finalUsage?.contextWindowUsed).toBe(60000)
  })
})
