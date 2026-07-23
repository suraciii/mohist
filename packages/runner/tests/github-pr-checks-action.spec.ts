import { afterEach, describe, expect, it, vi } from "vitest"
import { callAction } from "./support/call-action.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { setGitHubPrChecksTimingForTest, setGitHubPrGhRunnerForTest } from "../src/actions/github-pr.js"
import { githubPrChecksAction } from "../src/actions/github-pr-checks-action.js"
import type { JsonObject } from "../src/core/types.js"
import type { ActionTestContext as ActionContext } from "./support/action-test-context.js"
import {
  checksRollup,
  createMergeGhTestHarness,
  ghFail,
  ghOk,
  type CommandResult,
} from "./support/merge-github-pr-test-helpers.js"

const { ghCalls, installGh } = createMergeGhTestHarness()

afterEach(() => {
  ghCalls.length = 0
})

const WORKSPACE_PATH = "/workspace"

function prChecksContext(withOverrides: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-check-1",
    workId: "verify-pr-checks",
    workType: "task",
    stage: "check",
    title: "Verify GitHub PR checks",
    uses: "mohist/github-pr-checks",
    with: { repositoryUrl: "https://github.com/acme/repo.git", ...withOverrides },
    variables: {},
    workDir: WORKSPACE_PATH,
    projectId: "proj_1",
    issueNumber: 460,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function installGhFlat(responses: Record<string, () => CommandResult>): string[] {
  const calls: string[] = []
  setGitHubPrGhRunnerForTest(async (cmd, args, _cwd, _signal, _env, options) => {
    const full = [cmd, ...args].join(" ")
    calls.push(full)
    ghCalls.push({ command: full, timeoutMs: options?.timeoutMs })
    const responder = responses[full]
    if (!responder) return ghFail(`unexpected gh call: ${full}`)
    return responder()
  })
  return calls
}

describe("mohist/github-pr-checks registry", () => {
  it("registers mohist/github-pr-checks", () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve("mohist/github-pr-checks")
    expect(resolved.kind).toBe("definition")
    if (resolved.kind === "definition") {
      expect(resolved.definition.manifest.name).toBe("mohist/github-pr-checks")
    }
  })
})

describe("mohist/github-pr-checks action", () => {
  it("verifies and returns status verified when all checks pass", async () => {
    const calls = installGhFlat({
      "gh --version": () => ghOk("gh version 2.40.0\n"),
      "gh auth status": () => ghOk("Logged in to github.com\n"),
      "gh pr view 42 --json statusCheckRollup --repo github.com/acme/repo": () =>
        ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }])),
    })

    const result = await callAction(githubPrChecksAction, prChecksContext({ prNumber: 42 }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: "github-pr-checks",
      status: "verified",
      prNumber: 42,
    })
    expect(calls).toContain("gh pr view 42 --json statusCheckRollup --repo github.com/acme/repo")
    expect(result.output).not.toBeNull()
  })

  it("fails with errorCode pr-checks-failed when a check is FAILURE/CANCELLED/ACTION_REQUIRED", async () => {
    for (const conclusion of ["FAILURE", "CANCELLED", "ACTION_REQUIRED"]) {
      installGhFlat({
        "gh --version": () => ghOk("ok\n"),
        "gh auth status": () => ghOk("ok\n"),
        "gh pr view 42 --json statusCheckRollup --repo github.com/acme/repo": () =>
          ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion }])),
      })

      const result = await callAction(githubPrChecksAction, prChecksContext({ prNumber: 42 }))

      expect(result.error).toMatchObject({ code: "pr-checks-failed" })
      expect(result.error?.message).toContain("PR #42 checks failed")
      expect(result.error?.message).toContain("build")
    }
  })

  it("polls while checks are pending, then verifies once they pass", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 5_000, unavailableRetryLimit: 3 })
    let polls = 0
    installGhFlat({
      "gh --version": () => ghOk("ok\n"),
      "gh auth status": () => ghOk("ok\n"),
      "gh pr view 42 --json statusCheckRollup --repo github.com/acme/repo": () => {
        polls += 1
        if (polls < 3) return ghOk(checksRollup([{ name: "build", status: "IN_PROGRESS" }]))
        return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
      },
    })

    const result = await callAction(githubPrChecksAction, prChecksContext({ prNumber: 42 }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({ status: "verified", prNumber: 42 })
    expect(polls).toBe(3)
  })

  it("polls an initially empty rollup until passing checks appear", async () => {
    vi.useFakeTimers()
    try {
      setGitHubPrChecksTimingForTest({ pollIntervalMs: 10, noChecksGraceMs: 100 })
      let polls = 0
      installGhFlat({
        "gh --version": () => ghOk("ok\n"),
        "gh auth status": () => ghOk("ok\n"),
        "gh pr view 42 --json statusCheckRollup --repo github.com/acme/repo": () => {
          polls += 1
          return polls < 3
            ? ghOk(checksRollup([]))
            : ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        },
      })

      const resultPromise = callAction(githubPrChecksAction, prChecksContext({ prNumber: 42 }))
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(20)
      const result = await resultPromise

      expect(result.error).toBeUndefined()
      expect(result.output).toMatchObject({ status: "verified", prNumber: 42 })
      expect(polls).toBe(3)
    } finally {
      vi.useRealTimers()
    }
  })

  it("returns pr-checks-unavailable when the rollup remains empty through the grace period", async () => {
    vi.useFakeTimers()
    try {
      setGitHubPrChecksTimingForTest({ pollIntervalMs: 10, noChecksGraceMs: 25 })
      let polls = 0
      installGhFlat({
        "gh --version": () => ghOk("ok\n"),
        "gh auth status": () => ghOk("ok\n"),
        "gh pr view 42 --json statusCheckRollup --repo github.com/acme/repo": () => {
          polls += 1
          return ghOk(checksRollup([]))
        },
      })

      const resultPromise = callAction(githubPrChecksAction, prChecksContext({ prNumber: 42 }))
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(30)
      const result = await resultPromise

      expect(result.error).toMatchObject({ code: "pr-checks-unavailable" })
      expect(result.error?.message).toContain("no PR checks were reported")
      expect(polls).toBe(4)
    } finally {
      vi.useRealTimers()
    }
  })

  it("fails with invalid-input when prNumber is missing", async () => {
    const result = await callAction(githubPrChecksAction, prChecksContext({}))
    expect(result.error).toMatchObject({ code: "invalid-input" })
  })

  it("fails with config-error when gh precheck fails", async () => {
    installGhFlat({
      "gh --version": () => ghOk("ok\n"),
      "gh auth status": () => ghFail("not logged in"),
    })

    const result = await callAction(githubPrChecksAction, prChecksContext({ prNumber: 42 }))
    expect(result.error).toMatchObject({ code: "config-error" })
  })

  it("fails with config-error when authoritative repository URL is unparseable", async () => {
    const ctx = prChecksContext({ repositoryUrl: "not-a-url", prNumber: 42 })
    const result = await callAction(githubPrChecksAction, ctx)
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("valid GitHub repository URL")
  })
})
