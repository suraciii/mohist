import { describe, expect, it } from "vitest"
import { acpAgentAction } from "../../src/actions/acp-agent.js"
import { contextWithOverrides, createSharedSessionFixture } from "./support.js"

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