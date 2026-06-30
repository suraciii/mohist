import { afterEach, describe, expect, it, vi } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { setIssueFieldCommandRunnerForTest } from "../src/actions/issue-fields.js"
import {
  mergeGitHubPrAction,
  setGitHubPrChecksTimingForTest,
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "../src/actions/github-pr.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }

const WORKSPACE_PATH = "/workspace"
const PROJECT_PATH = "/project"
const PR_CHECKS_COMMAND = "gh pr view 42 --json statusCheckRollup"

afterEach(() => {
  setGitHubPrGitRunnerForTest(null)
  setGitHubPrGhRunnerForTest(null)
  setGitHubPrChecksTimingForTest(null)
  setIssueFieldCommandRunnerForTest(null)
})

function ghOk(stdout: string, stderr = ""): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

function ghFail(stderr: string, stdout = "", exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

function checksRollup(checks: unknown[]): string {
  return JSON.stringify({ statusCheckRollup: checks })
}

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-merge-1",
    workId: "merge-pr",
    workType: "task",
    stage: "integrate",
    title: "Merge GitHub PR",
    uses: "mohist/merge-github-pr",
    with: withOverrides,
    variables: {
      project: { id: "proj_1", path: WORKSPACE_PATH },
      issue: { title: "Use GitHub PR workflow", body: "Open, review, and merge a GitHub PR.", number: 248 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
      },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-merge-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    projectId: "proj_1",
    issueNumber: 248,
    signal: new AbortController().signal,
    writeVars: async () => {},
  }
}

function installGit(respond: () => never) {
  setGitHubPrGitRunnerForTest(async () => await respond())
}

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setGitHubPrGhRunnerForTest(async (cmd, args, cwd, _signal) => await respond(cmd, args, cwd))
}

function installMoIssueShow(title = "Use GitHub PR workflow", body = "body") {
  const calls: string[] = []
  setIssueFieldCommandRunnerForTest(async (cmd, args) => {
    calls.push([cmd, ...args].join(" "))
    return {
      exitCode: 0,
      stdout: JSON.stringify({ success: true, data: { title, body } }),
      stderr: "",
    }
  })
  return calls
}

describe("mohist/merge-github-pr registry", () => {
  it("registers merge-github-pr and exposes it under the new id only", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/merge-github-pr")).toBe(mergeGitHubPrAction)
    expect(registry.resolve("mohist/merge-pull-request")).toBeUndefined()
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(moCalls).toEqual([
      "mo issue show 248 --project-id proj_1 --output json",
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
      errorCode: null,
      message: null,
    })
  })

  it("resolves an open PR from source/target when prNumber is omitted", async () => {
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

    const result = await mergeGitHubPrAction(context({
      source: "mohist/run-wr-merge-1",
      target: "master",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.prNumber).toBe(9)
    expect(output.mergeCommitSha).toBe("merge-sha-9")
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-github-pr",
      status: "failed",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      errorCode: "base-moved",
    })
    expect(output.message).toContain("gh pr merge 42 --squash failed")
    expect(output.message.length).toBeGreaterThan(0)
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

      const result = await mergeGitHubPrAction(context({
        prNumber: 42,
        method: "squash",
        subjectFrom: "issue.title",
      }))
      const output = JSON.parse(result.output ?? "{}")

      expect(result.status).toBe("failure")
      expect(output).toMatchObject({
        kind: "merge-github-pr",
        status: "failed",
        prNumber: 42,
        prUrl: "https://github.com/example/repo/pull/42",
        errorCode: "pr-checks-failed",
      })
      expect(output.message).toContain("PR #42 checks failed")
      expect(output.message.length).toBeGreaterThan(0)
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(checksCalls).toBeGreaterThanOrEqual(3)
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBeGreaterThanOrEqual(3)
    expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(output).toMatchObject({
      kind: "merge-github-pr",
      status: "completed",
      prNumber: 42,
      mergeCommitSha: "merge-sha-1",
      errorCode: null,
    })
  })

  it("proceeds to merge after the grace window when statusCheckRollup is readable and empty", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 5 })
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBeGreaterThan(1)
    expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(output).toMatchObject({ kind: "merge-github-pr", status: "completed", mergeCommitSha: "merge-sha-1" })
  })

  it("fails with pr-checks-unavailable when gh pr view cannot read check status even if output says no checks reported", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 5 })
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
          return ghFail(`no checks reported on the 'mohist/run-wr-merge-1' branch\n`)
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup").length).toBe(1)
    expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(output).toMatchObject({ kind: "merge-github-pr", status: "failed", errorCode: "pr-checks-unavailable" })
    expect(output.message).toContain("PR #42 checks status unavailable")
  })

  it("fails immediately with pr-checks-unavailable when gh pr view statusCheckRollup errors", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 60_000 })
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(ghCalls).not.toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
    expect(output).toMatchObject({ kind: "merge-github-pr", status: "failed", errorCode: "pr-checks-unavailable" })
    expect(output.message).toContain("PR #42 checks status unavailable")
  })

  it("fails with pr-checks-unavailable when gh pr view statusCheckRollup returns invalid JSON", async () => {
    setGitHubPrChecksTimingForTest({ pollIntervalMs: 1, noChecksGraceMs: 60_000 })
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({ kind: "merge-github-pr", status: "failed", errorCode: "pr-checks-unavailable" })
    expect(output.message).toContain("unparseable JSON")
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-github-pr",
      errorCode: "pr-state-conflict",
      prNumber: 42,
    })
    expect(output.message).toContain("PR #42 is closed")
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      subject: "Issue title",
    }, { project: { id: "proj_1", path: PROJECT_PATH } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
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

    const result = await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "merge",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-github-pr",
      errorCode: "config-error",
      prNumber: null,
    })
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
        const resultPromise = mergeGitHubPrAction(ctx)
        await vi.advanceTimersByTimeAsync(15_000)
        await vi.advanceTimersByTimeAsync(15_000)
        const result = await resultPromise
        const output = JSON.parse(result.output ?? "{}")

        expect(result.status).toBe("success")
        const checksCalls = ghCalls.filter((c) => c === "gh pr view 42 --json statusCheckRollup")
        expect(checksCalls.length).toBe(3)
        expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
        expect(output).toMatchObject({
          kind: "merge-github-pr",
          status: "completed",
          prNumber: 42,
          prUrl: "https://github.com/example/repo/pull/42",
          mergeCommitSha: "merge-sha-1",
          errorCode: null,
          message: null,
        })
        const stepNames = output.steps.map((step: { name: string }) => step.name)
        expect(stepNames.filter((name: string) => name === "gh-pr-checks").length).toBe(3)
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
        const resultPromise = mergeGitHubPrAction(ctx)
        const probe = resultPromise.then(
          () => "resolved" as const,
          (error: unknown) => ({ kind: "rejected" as const, error }),
        )
        await vi.advanceTimersByTimeAsync(15_000)
        controller.abort(new Error("run canceled"))
        const outcome = await probe
        const result = await resultPromise
        const output = JSON.parse(result.output ?? "{}")

        expect(outcome).toBe("resolved")
        expect(result.status).toBe("failure")
        expect(output).toMatchObject({
          kind: "merge-github-pr",
          errorCode: "retry-safe",
          prNumber: 42,
        })
        expect(output.message).toContain("Cancelled while waiting for PR #42 checks")
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

        const result = await mergeGitHubPrAction(context({
          prNumber: 42,
          method: "squash",
          subjectFrom: "issue.title",
        }))
        const output = JSON.parse(result.output ?? "{}")

        expect(result.status).toBe("success")
        expect(ghCalls).toContain("gh pr view 42 --json statusCheckRollup")
        expect(ghCalls).toContain("gh pr merge 42 --squash --subject Use GitHub PR workflow --body ")
        expect(output).toMatchObject({
          kind: "merge-github-pr",
          status: "completed",
          prNumber: 42,
          errorCode: null,
        })
      }
    })
  })
})
