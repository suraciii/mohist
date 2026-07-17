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

  it("DoesNotPerformExpectationRepair_WhenExpectationUnmet", async () => {
    // The Workflow-owned completion evaluator (run by the executor, not
    // the Action) owns file/marker/failIf/_output evaluation. The
    // Action MUST NOT inspect `with.expect` or schedule an implicit
    // repair turn. To prove that, this fixture supplies `with.expect`
    // (the legacy shape) and verifies that the Action completes the
    // agent turn exactly once, regardless of whether the artifact was
    // produced. Spec scenario: "An unmet expectation does not trigger
    // an implicit repair turn" + "Runner spec and unit tests pass;
    // tests that locked the old Action-private verifyExpectations or
    // acp-agent output shape are updated" (T-003 acceptance).
    const fixture = createFixture("basic")

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
    }))

    expect(result.status).toBe("success")
    expect(fixture.agent.calls.filter((entry) => entry.event === "prompt")).toHaveLength(1)
    // The output no longer carries the legacy verification /
    // failIfMarker / promise fields — the Workflow completion
    // evaluator owns those (design D5).
    const output = JSON.parse(result.output ?? "{}")
    expect(output.expectation).toBeUndefined()
    expect(output.promise).toBeUndefined()
    expect(output.failIfMarker).toBeUndefined()
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
})

