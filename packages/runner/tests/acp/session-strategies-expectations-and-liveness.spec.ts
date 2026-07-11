import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
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
})
