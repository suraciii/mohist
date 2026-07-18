import { describe, expect, it } from "vitest"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import { mergeGitHubPrAction, setGitHubPrTransientRetryForTest } from "../src/actions/github-pr.js"
import {
  type CommandResult,
  checksRollup,
  context,
  createMergeGhTestHarness,
  ghFail,
  ghOk,
  ghTimeout,
} from "./support/merge-github-pr-test-helpers.js"

const { ghCalls, installGit, installGh, installMoIssueShow } = createMergeGhTestHarness()

describe("mohist/merge-github-pr transient network retry", () => {
  function installHappyMergeGh(mergeStateRespond: (calls: number) => CommandResult) {
    let mergeStateCalls = 0
    const localCalls: string[] = []
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      localCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr view 42 --json statusCheckRollup":
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        case "gh pr view 42 --json mergeStateStatus": {
          mergeStateCalls++
          return mergeStateRespond(mergeStateCalls)
        }
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })
    return {
      mergeStateCallCount: () => mergeStateCalls,
      ghCalls: () => localCalls,
    }
  }

  it("retries a transient network error (unexpected EOF) on the mergeStateStatus poll then merges", async () => {
    setGitHubPrTransientRetryForTest({ limit: 2, backoffMs: 0 })
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    const { mergeStateCallCount } = installHappyMergeGh((calls) =>
      calls === 1
        ? ghFail(`Post "https://api.github.com/graphql": unexpected EOF`)
        : ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" })),
    )

    const result = await mergeGitHubPrAction(context({ prNumber: 42, method: "squash", subjectFrom: "issue.title" }))

    expect(result.status).toBe("success")
    expect(mergeStateCallCount()).toBe(2)
  })

  it("surfaces a retry-safe failure after exhausting transient retries on a read", async () => {
    setGitHubPrTransientRetryForTest({ limit: 2, backoffMs: 0 })
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    const { mergeStateCallCount } = installHappyMergeGh(() =>
      ghFail(`Post "https://api.github.com/graphql": unexpected EOF`),
    )

    const result = await mergeGitHubPrAction(context({ prNumber: 42, method: "squash", subjectFrom: "issue.title" }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("retry-safe")
    // 1 initial attempt + 2 retries.
    expect(mergeStateCallCount()).toBe(3)
  })

  it("does not retry non-transient gh read failures", async () => {
    setGitHubPrTransientRetryForTest({ limit: 3, backoffMs: 0 })
    const localCalls: string[] = []
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      localCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghFail("HTTP 404: Not Found")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await mergeGitHubPrAction(context({ prNumber: 42, method: "squash", subjectFrom: "issue.title" }))

    expect(result.status).toBe("failure")
    expect(localCalls.filter((c) => c === "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus").length).toBe(1)
  })
})

describe("mohist/merge-github-pr network timeouts", () => {
  it("NetworkGhCalls_AllReceiveTimeoutMs", async () => {
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

    await mergeGitHubPrAction(context({
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))

    const networkCommands = [
      "gh --version",
      "gh auth status",
      "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus",
      "gh pr view 42 --json statusCheckRollup",
      "gh pr view 42 --json mergeStateStatus",
      "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ",
      "gh pr view 42 --json state,mergeCommit,url",
    ]
    for (const command of networkCommands) {
      const call = ghCalls.find((c) => c.command === command)
      expect(call?.timeoutMs, `gh call ${command} missing timeoutMs`).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    }
  })

  it("GhPrMergeTimeout_ClassifiesAsRetrySafeAndSurfacesDuration", async () => {
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
          return ghOk(checksRollup([{ name: "build", status: "COMPLETED", conclusion: "SUCCESS" }]))
        case "gh pr view 42 --json mergeStateStatus":
          return ghOk(JSON.stringify({ mergeStateStatus: "CLEAN" }))
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghTimeout()
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
    expect(output.errorCode).toBe("retry-safe")
    expect(output.output).toContain("timed out")
    const mergeStep = output.steps.find((step: { name: string }) => step.name === "gh-pr-merge")
    expect(mergeStep).toBeDefined()
    expect(mergeStep.output).toContain("timed out")
    expect(mergeStep.exitCode).toBe(124)
  })

  it("GhPrViewTimeout_IsNotRetriedAndSurfacesDuration", async () => {
    installMoIssueShow()
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus":
          return ghTimeout()
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
    expect(output.errorCode).toBe("retry-safe")
    expect(ghCalls.filter((c) => c.command === "gh pr view 42 --json state,mergeCommit,url,number,mergeStateStatus")).toHaveLength(1)
    const viewStep = output.steps.find((step: { name: string }) => step.name === "gh-pr-view")
    expect(viewStep).toMatchObject({
      command: "pr view 42 --json state,mergeCommit,url,number,mergeStateStatus",
      exitCode: 124,
      status: "timeout",
      timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
    })
    expect(viewStep.output).toContain("timed out")
  })
})
