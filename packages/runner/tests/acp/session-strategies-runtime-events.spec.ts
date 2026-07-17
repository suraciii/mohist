import { afterEach, describe, expect, it, vi } from "vitest"
import { setAcpProcessFactoryForTest } from "../../src/actions/acp-agent.js"
import { setPromptLoaderRegistryForTest } from "../../src/core/prompt.js"
import { createFixture, resetAcpTestHooks, useAcpFakeTimers } from "./support.js"
import { runWithProviderDefaultModelWarning } from "./session-strategies-test-support.js"

afterEach(() => {
  setAcpProcessFactoryForTest(null)
  setPromptLoaderRegistryForTest(null)
  resetAcpTestHooks()
})

describe("mohist/acp-agent new and ephemeral sessions", () => {
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
    expect(resolvedModelEvent?.runtimeSessionId).toBe("fake-session-1")
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
