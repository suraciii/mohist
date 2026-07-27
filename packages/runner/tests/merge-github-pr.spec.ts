import { describe, expect, it, vi } from "vitest"
import { callAction } from "./support/call-action.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { mergeGitHubPrAction, setGitHubPrChecksTimingForTest, setGitHubPrGhRunnerForTest } from "../src/actions/github-pr.js"
import {
  PR_CHECKS_COMMAND,
  PROJECT_PATH,
  WORKSPACE_PATH,
  checksRollup,
  context,
  createMergeGhTestHarness,
  ghFail,
  ghOk,
  withLog,
} from "./support/merge-github-pr-test-helpers.js"

const { ghCalls, installGit, installGh, installMoIssueShow } = createMergeGhTestHarness()

describe("mohist/merge-github-pr registry", () => {
  it("registers merge-github-pr and exposes it under the new id only", () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve("mohist/merge-github-pr")
    expect(resolved.kind).toBe("definition")
    if (resolved.kind === "definition") {
      expect(resolved.definition.manifest.name).toBe("mohist/merge-github-pr")
    }
    expect(registry.resolve("mohist/merge-pull-request").kind).toBe("unknown")
  })
})

describe("mohist/merge-github-pr action", () => {
  it("waits for checks, merges via squash, re-queries MERGED, and returns prNumber/prUrl/mergeCommitSha", async () => {
    const ghCalls: string[] = []
    const moCalls = installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
         case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
         case "gh pr view 42 --json statusCheckRollup":
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
         case "gh pr view 42 --json mergeStateStatus":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
         case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghOk("Merged pull request #42\n")
         case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(moCalls).toEqual([
      "mo issue show 248 --project proj_1 --json title,body",
    ])
    expect(ghCalls).toEqual([
      "gh --version",
      "gh auth status",
      "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus",
      "gh pr view 42 --json statusCheckRollup",
      "gh pr view 42 --json mergeStateStatus",
      "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ",
      "gh pr view 42 --json state,mergeCommit,url",
    ])
    expect(output).toMatchObject({
      kind: "merge-github-pr",
      status: "completed",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      mergeCommitSha: "merge-sha-1",
      method: "squash",
      output: "Merged PR #42 via squash with subject \"Use GitHub PR workflow\"",
    })
  })

  it("uses the explicitly declared repository despite different Variables", async () => {
    const commands: string[] = []
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      commands.push(full)
      if (full === "gh --version" || full === "gh auth status") return ghOk("ok\n")
       if (full === "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus") {
        return ghOk(JSON.stringify({ state: "MERGED", number: 42, url: "https://github.com/acme/repo/pull/42", mergeCommit: { oid: "merge-sha" } }))
      }
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await callAction(mergeGitHubPrAction, context({ repositoryUrl: "https://github.com/acme/repo.git", prNumber: 42, method: "squash", subject: "Issue title" }, { repository: { gitUrl: "https://example.com/other.git" } }))

    expect(result.error).toBeUndefined()
     expect(commands).toContain("gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus")
  })

  it("rejects an invalid explicit repository URL", async () => {
    const result = await callAction(mergeGitHubPrAction, context({ repositoryUrl: "not a Git URL", prNumber: 42, method: "squash", subject: "Issue title" }))
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("valid GitHub repository URL")
  })

  it("forwards gh command output to the task log sink", async () => {
    const writes: Array<{ source: string; text: string }> = []
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    setGitHubPrGhRunnerForTest(async (cmd, args, _cwd, _signal, _env, options) => {
      const full = [cmd, ...args].join(" ")
      options?.onLine?.(`captured ${full}`)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
         case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus --repo github.com/example/repo":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
         case "gh pr view 42 --json statusCheckRollup --repo github.com/example/repo":
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
         case "gh pr view 42 --json mergeStateStatus --repo github.com/example/repo":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
         case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body  --repo github.com/example/repo":
          return ghOk("Merged pull request #42\n")
         case "gh pr view 42 --json state,mergeCommit,url --repo github.com/example/repo":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, withLog(context({ prNumber: 42, method: "squash", subjectFrom: "issue.title" }), writes))

    expect(result.error).toBeUndefined()
    expect(writes.some((write) => write.source === "action:merge-github-pr" && write.text.includes("gh pr merge 42"))).toBe(true)
  })

  it("requires an explicit PR number instead of discovering one from source/target", async () => {
    installMoIssueShow()
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr list --head mohist/run-wr-merge-1 --base master --state open --json number,url":
          return ghOk(JSON.stringify([{ number: 9, url: "https://github.com/example/repo/pull/9" }]))
        case "gh pr view 9 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "MERGED", number: 9, url: "https://github.com/example/repo/pull/9", mergeCommit: { oid: "merge-sha-9" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      source: "mohist/run-wr-merge-1",
      target: "master",
      subjectFrom: "issue.title",
    }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toMatchObject({ code: "invalid-input" })
    expect(result.error?.message).toContain("prNumber")
    expect(ghCalls).toEqual([])
  })

  it("fails with errorCode base-moved and includes prNumber/prUrl/message when gh pr merge rejects the merge", async () => {
    installMoIssueShow()
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        case "gh pr view 42 --json mergeStateStatus":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghFail("GraphQL: Pull request is not mergeable")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    expect(result.error).toMatchObject({ code: "base-moved" })
    expect(result.error?.message).toContain("gh pr merge 42 --squash failed")
  })

  it("reports base-moved before reading old failing checks when the PR is behind", async () => {
    const ghCalls: string[] = []
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({
            state: "OPEN",
            number: 42,
            url: "https://github.com/example/repo/pull/42",
            mergeCommit: null,
            mergeStateStatus: "BEHIND",
          }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    expect(result.error).toMatchObject({ code: "base-moved" })
    expect(result.error?.message).toContain("PR #42 is BEHIND; rebase required.")
    expect(ghCalls).not.toContain(PR_CHECKS_COMMAND)
    expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
  })

  it("fails with errorCode pr-checks-failed when a check is FAIL/CANCELLED/ACTION_REQUIRED and skips the merge call", async () => {
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })

    const failureCases: string[] = [
      "FAILURE",
      "CANCELLED",
      "ACTION_REQUIRED",
    ]

    for (const conclusion of failureCases) {
      const ghCalls: string[] = []
      installGh((cmd, args) => {
        const full = [cmd, ...args].join(" ")
        ghCalls.push(full)
        switch (full) {
          case "gh --version":
          case "gh auth status":
            return ghOk("ok\n")
          case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
            return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
          case "gh pr view 42 --json statusCheckRollup":
            return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion }]))
          default:
            return ghFail(`unexpected gh call: ${full}`)
        }
      })

      const result = await callAction(mergeGitHubPrAction, context({
        prNumber: 42,
        method: "squash",
        subjectFrom: "issue.title",
      }))
      expect(result.error).toMatchObject({ code: "pr-checks-failed" })
      expect(result.error?.message).toContain("PR #42 checks failed")
      expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    }
  })

  it("treats an empty statusCheckRollup as transient and keeps polling until checks pass", async () => {
    // Right after a push / force-push, GitHub hasn't registered the workflow
    // run as a check run yet, so the PR statusCheckRollup can be empty. This
    // is transient and must be polled, not failed.
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 60_000 })
    const ghCalls: string[] = []
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    let checksCalls = 0
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          checksCalls += 1
          if (checksCalls < 3) {
            return ghOk(checksRollup([]))
          }
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        case "gh pr view 42 --json mergeStateStatus":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(checksCalls).toBeGreaterThanOrEqual(3)
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBeGreaterThanOrEqual(3)
    expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(output).toMatchObject({
      kind: "merge-github-pr",
      status: "completed",
      prNumber: 42,
      mergeCommitSha: "merge-sha-1",
    })
  })

  it("returns pr-checks-unavailable without merging when the rollup remains empty through the grace period", async () => {
    vi.useFakeTimers()
    try {
      setGitHubPrChecksTimingForTest({ pollIntervalMs: 10, noChecksGraceMs: 25 })
      const ghCalls: string[] = []
      installMoIssueShow()
      installGit(() => { throw new Error("git should not be called") })
      installGh((cmd, args) => {
        const full = [cmd, ...args].join(" ")
        ghCalls.push(full)
        switch (full) {
          case "gh --version":
          case "gh auth status":
            return ghOk("ok\n")
          case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
            return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
          case "gh pr view 42 --json statusCheckRollup":
            return ghOk(checksRollup([]))
          default:
            return ghFail(`unexpected gh call: ${full}`)
        }
      })

      const resultPromise = callAction(mergeGitHubPrAction, context({
        prNumber: 42,
        method: "squash",
        subjectFrom: "issue.title",
      }))
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(30)
      const result = await resultPromise

      expect(result.error).toMatchObject({ code: "pr-checks-unavailable" })
      expect(result.error?.message).toContain("no PR checks were reported")
      expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    } finally {
      vi.useRealTimers()
    }
  })

  it("retries transient pr-checks-unavailable inside the action before merging", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 5, unavailableRetryLimit: 3 })
    const ghCalls: string[] = []
    let checksCalls = 0
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          checksCalls += 1
          if (checksCalls < 3) {
            return ghFail(`GraphQL: failed to read statusCheckRollup\n`)
          }
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        case "gh pr view 42 --json mergeStateStatus":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBe(3)
    expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(output).toMatchObject({ kind: "merge-github-pr", status: "completed", mergeCommitSha: "merge-sha-1" })
  })

  it("fails with pr-checks-unavailable after internal statusCheckRollup retries are exhausted", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 60_000, unavailableRetryLimit: 2 })
    const ghCalls: string[] = []
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          return ghFail("GraphQL: could not resolve check runs\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    expect(result.error).toMatchObject({ code: "pr-checks-unavailable" })
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBe(3)
    expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(result.error?.message).toContain("PR #42 checks status unavailable")
    expect(result.error?.message).toContain("after 3 attempts")
  })

  it("fails with pr-checks-unavailable when gh pr view statusCheckRollup returns invalid JSON", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 60_000, unavailableRetryLimit: 1 })
    const ghCalls: string[] = []
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          return ghOk("not json")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    expect(result.error).toMatchObject({ code: "pr-checks-unavailable" })
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBe(2)
    expect(result.error?.message).toContain("unparseable JSON")
  })

  it("reports pr-state-conflict when the PR is closed", async () => {
    installMoIssueShow()
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "CLOSED", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      subjectFrom: "issue.title",
    }))
    expect(result.error).toMatchObject({ code: "pr-state-conflict" })
    expect(result.error?.message).toContain("PR #42 is closed")
  })

  it("binds every gh call to the workspace path even when project.path differs", async () => {
    const ghCalls: Array<{ cwd: string; command: string }> = []
    installGh((cmd, args, cwd) => {
      const command = [cmd, ...args].join(" ")
      ghCalls.push({ cwd, command })
      switch (command) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        case "gh pr view 42 --json mergeStateStatus":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
        case "gh pr merge 42 --squash --subject Issue title --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${command}`)
      }
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      subject: "Issue title",
    }, { project: { id: "proj_1", path: PROJECT_PATH } }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls.every((call) => call.cwd === WORKSPACE_PATH)).toBe(true)
    expect(ghCalls.some((call) => call.cwd === PROJECT_PATH)).toBe(false)
    expect(output.mergeCommitSha).toBe("merge-sha-1")
  })

  it("rejects non-squash methods as config-error before any gh mutation", async () => {
    const ghCalls: string[] = []
    installGh((cmd, args) => {
      ghCalls.push([cmd, ...args].join(" "))
      return ghOk("ok\n")
    })

    const result = await callAction(mergeGitHubPrAction, context({
      prNumber: 42,
      method: "merge",
    }))
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(ghCalls).toEqual([])
  })

  describe("checks-gated merge", () => {
    it("waits through pending checks and merges once a check passes", async () => {
      vi.useFakeTimers()
      try {
        const ghCalls: string[] = []
        installMoIssueShow()
        installGit(() => { throw new Error("git should not be called") })
        installGh((cmd, args) => {
          const full = [cmd, ...args].join(" ")
          ghCalls.push(full)
          switch (full) {
            case "gh --version":
            case "gh auth status":
              return ghOk("ok\n")
            case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
              return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
            case "gh pr view 42 --json statusCheckRollup": {
              const checksCount = ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length
              if (checksCount < 3) {
                return ghOk(checksRollup([
                  { name: "build", status: "IN_PROGRESS", conclusion: "" },
                  { name: "lint", status: "QUEUED", conclusion: "" },
                ]))
              }
              return ghOk(checksRollup([
                { name: "build", status: "COMPLETED", conclusion: "SUCCESS" },
                { name: "lint", status: "COMPLETED", conclusion: "SUCCESS" },
              ]))
            }
            case "gh pr view 42 --json mergeStateStatus":
              return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
            case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
              return ghOk("Merged pull request #42\n")
            case "gh pr view 42 --json state,mergeCommit,url":
              return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const ctx = context({
          prNumber: 42,
          method: "squash",
          subjectFrom: "issue.title",
        })
        const resultPromise = callAction(mergeGitHubPrAction, ctx)
        await vi.advanceTimersByTimeAsync(15_000)
        await vi.advanceTimersByTimeAsync(15_000)
        const result = await resultPromise
        const output = result.output as Record<string, unknown>

        expect(result.error).toBeUndefined()
        const checksCalls = ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup")
        expect(checksCalls.length).toBe(3)
        expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
        expect(output).toMatchObject({
          kind: "merge-github-pr",
          status: "completed",
          prNumber: 42,
          prUrl: "https://github.com/example/repo/pull/42",
          mergeCommitSha: "merge-sha-1",
        })
        const stepNames = (output.steps as Array<{ name: string }>).map((step) => step.name)
        expect(stepNames.filter((name: string) => name === "gh-pr-checks").length).toBe(3)
      } finally {
        vi.useRealTimers()
      }
    })

    it("waits through UNKNOWN mergeStateStatus after checks pass instead of returning a retryable failure", async () => {
      vi.useFakeTimers()
      try {
        const ghCalls: string[] = []
        installMoIssueShow()
        installGit(() => { throw new Error("git should not be called") })
        let mergeStateCalls = 0
        installGh((cmd, args) => {
          const full = [cmd, ...args].join(" ")
          ghCalls.push(full)
          switch (full) {
            case "gh --version":
            case "gh auth status":
              return ghOk("ok\n")
            case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
              return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
            case "gh pr view 42 --json statusCheckRollup":
              return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
            case "gh pr view 42 --json mergeStateStatus":
              mergeStateCalls += 1
              return ghOk(JSON.stringify({ mergeStateStatus: mergeStateCalls < 3 ? "UNKNOWN" : "CLEAN" }))
            case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
              return ghOk("Merged pull request #42\n")
            case "gh pr view 42 --json state,mergeCommit,url":
              return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const ctx = context({
          prNumber: 42,
          method: "squash",
          subjectFrom: "issue.title",
        })
        const resultPromise = callAction(mergeGitHubPrAction, ctx)
        await vi.advanceTimersByTimeAsync(15_000)
        await vi.advanceTimersByTimeAsync(15_000)
        const result = await resultPromise
        const output = result.output as Record<string, unknown>

        expect(result.error).toBeUndefined()
        expect(mergeStateCalls).toBe(3)
        expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
        expect(output).toMatchObject({
          kind: "merge-github-pr",
          status: "completed",
          prNumber: 42,
          mergeCommitSha: "merge-sha-1",
        })
      } finally {
        vi.useRealTimers()
      }
    })

    it("cancels waiting when the context signal is aborted while checks are still pending", async () => {
      vi.useFakeTimers()
      try {
        const ghCalls: string[] = []
        installMoIssueShow()
        installGit(() => { throw new Error("git should not be called") })
        installGh((cmd, args) => {
          const full = [cmd, ...args].join(" ")
          ghCalls.push(full)
          switch (full) {
            case "gh --version":
            case "gh auth status":
              return ghOk("ok\n")
            case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
              return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
            case "gh pr view 42 --json statusCheckRollup":
              return ghOk(checksRollup([{ name: "build", status: "IN_PROGRESS", conclusion: "" }]))
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const controller = new AbortController()
        const ctx = context({
          prNumber: 42,
          method: "squash",
          subjectFrom: "issue.title",
        })
        Object.assign(ctx, { signal: controller.signal })
        const resultPromise = callAction(mergeGitHubPrAction, ctx)
        const probe = resultPromise.then(
          () => "resolved" as const,
          (error: unknown) => ({ kind: "rejected" as const, error }),
        )
        await vi.advanceTimersByTimeAsync(15_000)
        controller.abort(new Error("run canceled"))
        const outcome = await probe
        const result = await resultPromise
        expect(outcome).toBe("resolved")
        expect(result.error).toMatchObject({ code: "retry-safe" })
        expect(result.error?.message).toContain("Cancelled while waiting for PR #42 checks")
        expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
      } finally {
        vi.useRealTimers()
      }
    })

    it("merges when all checks are PASS/SKIP", async () => {
      const cases: Array<{ checks: unknown[] }> = [
        { checks: [
          { name: "build", status: "COMPLETED", conclusion: "SUCCESS" },
          { name: "lint", status: "COMPLETED", conclusion: "SKIPPED" },
        ] },
      ]

      for (const scenario of cases) {
        const ghCalls: string[] = []
        installMoIssueShow()
        installGh((cmd, args) => {
          const full = [cmd, ...args].join(" ")
          ghCalls.push(full)
          switch (full) {
            case "gh --version":
            case "gh auth status":
              return ghOk("ok\n")
            case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
              return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
            case "gh pr view 42 --json statusCheckRollup":
              return ghOk(checksRollup(scenario.checks))
            case "gh pr view 42 --json mergeStateStatus":
              return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
            case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
              return ghOk("Merged pull request #42\n")
            case "gh pr view 42 --json state,mergeCommit,url":
              return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
            default:
              return ghFail(`unexpected gh call: ${full}`)
          }
        })

        const result = await callAction(mergeGitHubPrAction, context({
          prNumber: 42,
          method: "squash",
          subjectFrom: "issue.title",
        }))
        const output = result.output as Record<string, unknown>

        expect(result.error).toBeUndefined()
        expect(ghCalls).toContain("gh pr view 42 --json statusCheckRollup")
        expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
        expect(output).toMatchObject({
          kind: "merge-github-pr",
          status: "completed",
          prNumber: 42,
        })
      }
    })
  })
})
