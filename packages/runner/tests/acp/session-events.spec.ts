import { describe, expect, it, vi } from "vitest"
import { acpAgentAction } from "../../src/actions/acp-agent.js"
import { stringInput } from "../../src/core/json.js"
import { contextWithOverrides, createSharedSessionFixture } from "./support.js"

async function runWithProviderDefaultModelWarning<T>(context: Parameters<typeof acpAgentAction>[0], operation: () => Promise<T>): Promise<T> {
  const warningSpy = vi.spyOn(console, "warn").mockClear().mockImplementation(() => undefined)
  try {
    const result = await operation()

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

function providerDefaultModelWarningContext(context: Parameters<typeof acpAgentAction>[0]) {
  return {
    workflowRunId: context.workflowRunId,
    workId: context.workId,
    stage: context.stage,
    sessionName: stringInput(context.with, "session") ?? context.workId,
    requestedModel: null,
    requestedModelSource: "none",
  }
}

describe("mohist/acp-agent shared session observability", () => {
  it("ResumedSessionExposesCurrentModelId_RunnerEmitsResolvedModelEventWithResumeSource", async () => {
    const shared = createSharedSessionFixture("resolved-model", { sessionRecord: { runtimeSessionId: "server-session-1" } })

    const context = contextWithOverrides({
      prompt: "resume and report",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context())
    const result = await runWithProviderDefaultModelWarning(context, () => acpAgentAction(context))

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
    const shared = createSharedSessionFixture("resolved-model", { sessionRecord: { runtimeSessionId: "server-session-1" } })

    const context = contextWithOverrides({
      prompt: "resume with defaults",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
    }, undefined, shared.context())
    const result = await runWithProviderDefaultModelWarning(context, () => acpAgentAction(context))

    expect(result.status).toBe("success")
    const resumeCall = shared.agent.calls.find((entry) => entry.event === "resumeSession")
    expect(resumeCall).toBeTruthy()
    const meta = resumeCall?._meta as Record<string, unknown> | undefined
    expect(meta).toBeTruthy()
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.8, strategy: "summary" })
  })

  it("CompactionConfigExplicitlySet_ForwardedToResumeSessionMeta", async () => {
    const shared = createSharedSessionFixture("resolved-model", { sessionRecord: { runtimeSessionId: "server-session-1" } })

    const context = contextWithOverrides({
      prompt: "resume with custom compaction",
      session: "shared-session",
      livenessQuietThresholdMs: 5_000,
      probeTimeoutMs: 5_000,
      timeout: 5_000,
      compaction: { threshold: 0.65, strategy: "summary" },
    }, undefined, shared.context())
    const result = await runWithProviderDefaultModelWarning(context, () => acpAgentAction(context))

    expect(result.status).toBe("success")
    const resumeCall = shared.agent.calls.find((entry) => entry.event === "resumeSession")
    const meta = resumeCall?._meta as Record<string, unknown> | undefined
    const compaction = meta?.["opencode.compaction"] as Record<string, unknown> | undefined
    expect(compaction).toEqual({ threshold: 0.65, strategy: "summary" })
  })
})
