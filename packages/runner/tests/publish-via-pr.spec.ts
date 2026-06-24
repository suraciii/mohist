import { afterEach, describe, expect, it } from "vitest"
import {
  classifyGhFailure,
  classifyPushFailure,
  extractPrNumberFromUrl,
  looksLikeAuthFailure,
  looksLikeBaseMoved,
  looksLikeProtectionConflict,
  looksLikePrStateConflict,
  looksLikeRetrySafe,
  parsePrList,
  parsePrView,
  publishViaPrAction,
  setPublishViaPrGhRunnerForTest,
  setPublishViaPrGitRunnerForTest,
} from "../src/actions/publish-via-pr.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { setIssueFieldCommandRunnerForTest } from "../src/actions/issue-fields.js"
import type { ActionContext, JsonObject } from "../src/core/types.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }

type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

const WORKSPACE_PATH = "/workspace"

afterEach(() => {
  setPublishViaPrGitRunnerForTest(null)
  setPublishViaPrGhRunnerForTest(null)
  setIssueFieldCommandRunnerForTest(null)
})

function ok(stdout: string): GitResponse {
  return { success: true, stdout, stderr: "", exitCode: 0, combinedOutput: stdout.trim() }
}

function fail(stderr: string, stdout = ""): GitResponse {
  return { success: false, stdout, stderr, exitCode: 1, combinedOutput: [stdout.trim(), stderr.trim()].filter(Boolean).join("\n") }
}

function ghOk(stdout: string, stderr = ""): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

function ghFail(stderr: string, stdout = "", exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-pr-1",
    workId: "integrate:publish.1",
    workType: "task",
    stage: "integrate",
    title: "Publish changes",
    uses: "mohist/publish-via-pr",
    with: withOverrides,
    variables: {
      project: { path: WORKSPACE_PATH },
      issue: { title: "stale variable title", body: "stale variable body", number: 190 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
        name: "master",
      },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-pr-1" },
      mohist: { runId: "wr-pr-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    projectId: "proj_1",
    issueNumber: 190,
    signal: new AbortController().signal,
  }
}

function installGit(respond: (workDir: string, args: string[]) => GitResponse | Promise<GitResponse>) {
  setPublishViaPrGitRunnerForTest(respond)
}

function installGh(respond: (command: string, args: string[]) => CommandResult | Promise<CommandResult>) {
  setPublishViaPrGhRunnerForTest(async (cmd, args, _cwd, _signal) => await respond(cmd, args))
}

function installMoIssueShow(title = "PR delivery", body = "Implement the PR delivery workflow") {
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

describe("mohist/publish-via-pr action", () => {
  it("is registered under mohist/publish-via-pr in the default registry", () => {
    const registry = createDefaultRegistry()
    const handler = registry.resolve("mohist/publish-via-pr")
    expect(handler).toBeDefined()
    expect(handler).toBe(publishViaPrAction)
  })

  it("happy path: precheck, push, create PR, merge, and confirm state=merged", async () => {
    const gitCalls: string[] = []
    const ghCalls: string[] = []
    const moCalls = installMoIssueShow("PR delivery", "Implement the PR delivery workflow")

    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      switch (cmd) {
        case "fetch origin master":
          return ok("From https://example.com/repo.git\n * branch            master     -> FETCH_HEAD")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("To https://example.com/repo.git\n   abc123..def456  mohist/run-wr-pr-1 -> mohist/run-wr-pr-1")
        default:
          return fail(`unexpected git call: ${cmd}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0 (2024-10-09)\n")
        case "gh auth status":
          return ghOk("You are authenticated with GitHub as: octocat\n")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title PR delivery --body Implement the PR delivery workflow":
          return ghOk("https://github.com/example/repo/pull/42\n")
        case "gh pr view 42 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr merge 42 --squash --subject PR delivery --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context({
      source: "mohist/run-wr-pr-1",
      target: "master",
      remote: "origin",
      titleFrom: "issue.title",
      bodyFrom: "issue.body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(gitCalls).toEqual([
      "fetch origin master",
      "rev-parse origin/master",
      "push --force-with-lease origin mohist/run-wr-pr-1",
    ])
    expect(ghCalls).toEqual([
      "gh --version",
      "gh auth status",
      "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url",
      "gh pr create --head mohist/run-wr-pr-1 --base master --title PR delivery --body Implement the PR delivery workflow",
      "gh pr view 42 --json state,mergeCommit,url,number",
      "gh pr merge 42 --squash --subject PR delivery --body ",
      "gh pr view 42 --json state,mergeCommit,url",
    ])
    expect(moCalls).toEqual([
      "mo issue show 190 --project-id proj_1 --output json",
    ])
    expect(output).toMatchObject({
      kind: "publish-via-pr",
      status: "completed",
      source: "mohist/run-wr-pr-1",
      targetBranch: "master",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      mergeCommitSha: "merge-sha-1",
      baseSha: "base-sha-1",
      pushed: true,
      failureKind: null,
      failureMessage: null,
    })
    expect(output.steps.map((step: { name: string }) => step.name)).toEqual([
      "gh-precheck",
      "git-source-anchor",
      "git-fetch-base",
      "git-rev-parse-base",
      "git-push",
      "gh-pr-list",
      "gh-pr-create",
      "gh-pr-view",
      "gh-pr-merge",
      "gh-pr-view-confirm",
    ])
  })

  it("missing gh CLI fails fast with config-error and performs no remote mutation", async () => {
    const gitCalls: string[] = []
    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full === "gh --version") return ghFail("gh: command not found", "", 127)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(result.message).toContain("config-error")
    expect(result.message).toContain("gh auth login")
    expect(gitCalls).toEqual([])
    expect(output).toMatchObject({
      kind: "publish-via-pr",
      status: "failed",
      pushed: false,
      failureKind: "config-error",
    })
    expect(output.failureMessage).toContain("Install GitHub CLI")
    expect(output.failureMessage).toContain("gh auth login")
  })

  it("unauthenticated gh CLI fails fast with config-error and never pushes or creates a PR", async () => {
    const gitCalls: string[] = []
    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghFail("You are not logged into any GitHub hosts.", "", 1)
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("config-error")
    expect(output.failureMessage).toContain("gh auth login")
    expect(gitCalls).toEqual([])
  })

  it("issue title/body source failure reports config-error before pushing", async () => {
    const gitCalls: string[] = []
    const moCalls: string[] = []
    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      moCalls.push([cmd, ...args].join(" "))
      return {
        exitCode: 1,
        stdout: "",
        stderr: "issue not found",
      }
    })

    const result = await publishViaPrAction(context({
      titleFrom: "issue.title",
      bodyFrom: "issue.body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("config-error")
    expect(output.failureMessage).toContain("mo issue show 190 failed")
    expect(output.failureMessage).toContain("issue not found")
    expect(gitCalls).toEqual([])
    expect(moCalls).toEqual(["mo issue show 190 --project-id proj_1 --output json"])
  })

  it("unsupported issue field source reports config-error before pushing", async () => {
    const gitCalls: string[] = []
    const moCalls: string[] = []
    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      return fail(`unexpected git call: ${args.join(" ")}`)
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })
    setIssueFieldCommandRunnerForTest(async (cmd, args) => {
      moCalls.push([cmd, ...args].join(" "))
      return {
        exitCode: 0,
        stdout: "unexpected",
        stderr: "",
      }
    })

    const result = await publishViaPrAction(context({
      titleFrom: "issue.summary",
      bodyFrom: "issue.body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("config-error")
    expect(output.failureMessage).toContain("Unsupported titleFrom source 'issue.summary'")
    expect(gitCalls).toEqual([])
    expect(moCalls).toEqual([])
  })

  it("reuses an existing open PR and does not create a duplicate", async () => {
    const gitCalls: string[] = []
    const ghCalls: string[] = []

    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk(JSON.stringify([{ number: 7, url: "https://github.com/example/repo/pull/7" }]))
        case "gh pr view 7 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 7, url: "https://github.com/example/repo/pull/7", mergeCommit: null }))
        case "gh pr merge 7 --squash --subject Complete issue #190 --body ":
          return ghOk("merged")
        case "gh pr view 7 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", number: 7, url: "https://github.com/example/repo/pull/7", mergeCommit: { oid: "merge-sha-2" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls).toContain("gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url")
    expect(ghCalls.some((c) => c.startsWith("gh pr create"))).toBe(false)
    expect(output).toMatchObject({
      prNumber: 7,
      prUrl: "https://github.com/example/repo/pull/7",
      mergeCommitSha: "merge-sha-2",
      pushed: true,
    })
  })

  it("observes PR in state=MERGED before merge and returns success without calling gh pr merge", async () => {
    const gitCalls: string[] = []
    const ghCalls: string[] = []

    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk(JSON.stringify([{ number: 9, url: "https://github.com/example/repo/pull/9" }]))
        case "gh pr view 9 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "MERGED", number: 9, url: "https://github.com/example/repo/pull/9", mergeCommit: { oid: "merge-sha-3" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls.some((c) => c.startsWith("gh pr merge "))).toBe(false)
    expect(ghCalls.some((c) => c.startsWith("gh pr view 9 --json state,mergeCommit,url-confirm"))).toBe(false)
    expect(output).toMatchObject({
      prNumber: 9,
      prUrl: "https://github.com/example/repo/pull/9",
      mergeCommitSha: "merge-sha-3",
      pushed: true,
      failureKind: null,
    })
  })

  it("PR in state=CLOSED reports pr-state-conflict and does not merge", async () => {
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title Complete issue #190 --body Mohist issue #190":
          return ghOk("https://github.com/example/repo/pull/11\n")
        case "gh pr view 11 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "CLOSED", number: 11, url: "https://github.com/example/repo/pull/11", mergeCommit: null }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("pr-state-conflict")
    expect(output.prNumber).toBe(11)
    expect(output.prUrl).toBe("https://github.com/example/repo/pull/11")
  })

  it("base-moved (unmergeable) is reported and the action does not call gh pr merge again", async () => {
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    const ghCalls: string[] = []
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title Complete issue #190 --body Mohist issue #190":
          return ghOk("https://github.com/example/repo/pull/13\n")
        case "gh pr view 13 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 13, url: "https://github.com/example/repo/pull/13", mergeCommit: null }))
        case "gh pr merge 13 --squash --subject Complete issue #190 --body ":
          return ghFail("GraphQL: Pull request is not mergeable (Merge conflict)")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("base-moved")
    expect(output.prNumber).toBe(13)
    expect(ghCalls.filter((c) => c.startsWith("gh pr merge ")).length).toBe(1)
  })

  it("branch-protection rejection reports protection-conflict", async () => {
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title Complete issue #190 --body Mohist issue #190":
          return ghOk("https://github.com/example/repo/pull/15\n")
        case "gh pr view 15 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 15, url: "https://github.com/example/repo/pull/15", mergeCommit: null }))
        case "gh pr merge 15 --squash --subject Complete issue #190 --body ":
          return ghFail("GraphQL: Required status check \"build\" is expected.")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("protection-conflict")
    expect(output.prNumber).toBe(15)
  })

  it("transient network error reports retry-safe", async () => {
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghFail("API rate limit exceeded")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("retry-safe")
  })

  it("force-with-lease push that fails reports the classified failureKind", async () => {
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return fail("Everything up-to-date", "To https://example.com/repo.git\n ! [rejected]        mohist/run-wr-pr-1 -> mohist/run-wr-pr-1 (stale info)\n")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full === "gh --version") return ghOk("gh version 2.55.0\n")
      if (full === "gh auth status") return ghOk("ok")
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await publishViaPrAction(context())
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.failureKind).toBe("base-moved")
    expect(output.pushed).toBe(false)
  })

  it("uses repository.baseBranch and issue.number from variables when with is empty", async () => {
    const ghCalls: string[] = []
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin develop":
          return ok("")
        case "rev-parse origin/develop":
          return ok("base-sha-develop\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base develop --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base develop --title Complete issue #190 --body Mohist issue #190":
          return ghOk("https://github.com/example/repo/pull/33\n")
        case "gh pr view 33 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 33, url: "https://github.com/example/repo/pull/33", mergeCommit: null }))
        case "gh pr merge 33 --squash --subject Complete issue #190 --body ":
          return ghOk("merged")
        case "gh pr view 33 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", number: 33, url: "https://github.com/example/repo/pull/33", mergeCommit: { oid: "merge-sha-develop" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await publishViaPrAction(
      context(
        {},
        { repository: { gitUrl: "https://example.com/repo.git", baseBranch: "develop", name: "develop" } },
      ),
    )
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.targetBranch).toBe("develop")
    expect(output.baseSha).toBe("base-sha-develop")
    expect(ghCalls).toContain("gh pr list --head mohist/run-wr-pr-1 --base develop --state open --json number,url")
    expect(ghCalls).toContain("gh pr create --head mohist/run-wr-pr-1 --base develop --title Complete issue #190 --body Mohist issue #190")
  })

  it("never checks out the base branch in the workflow workspace", async () => {
    const gitCalls: string[] = []
    installGit((_workDir, args) => {
      gitCalls.push(args.join(" "))
      switch (args.join(" ")) {
        case "rev-parse --abbrev-ref HEAD":
          return ok("mohist/run-wr-pr-1\n")
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("ok")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("ok")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title Complete issue #190 --body Mohist issue #190":
          return ghOk("https://github.com/example/repo/pull/55\n")
        case "gh pr view 55 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 55, url: "https://github.com/example/repo/pull/55", mergeCommit: null }))
        case "gh pr merge 55 --squash --subject Complete issue #190 --body ":
          return ghOk("merged")
        case "gh pr view 55 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", number: 55, url: "https://github.com/example/repo/pull/55", mergeCommit: { oid: "merge-sha-55" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    await publishViaPrAction(context())

    for (const call of gitCalls) {
      expect(call.startsWith("checkout ")).toBe(false)
      expect(call.startsWith("merge ")).toBe(false)
      expect(call.startsWith("commit ")).toBe(false)
    }
    for (const call of gitCalls) {
      if (call.startsWith("push ")) {
        expect(call.startsWith("push --force-with-lease origin ")).toBe(true)
        expect(call).not.toContain("--delete")
      }
    }
  })
})

describe("mohist/publish-via-pr failure classifiers", () => {
  it("classifyGhFailure maps auth failures to config-error", () => {
    expect(classifyGhFailure("", "You are not logged into any GitHub hosts.")).toBe("config-error")
    expect(classifyGhFailure("Bad credentials", "")).toBe("config-error")
    expect(classifyGhFailure("", "Login required")).toBe("config-error")
  })

  it("classifyGhFailure maps merge-conflict / not-mergeable / out-of-date to base-moved", () => {
    expect(classifyGhFailure("", "Pull request is not mergeable")).toBe("base-moved")
    expect(classifyGhFailure("", "Merge conflict in README.md")).toBe("base-moved")
    expect(classifyGhFailure("", "The base branch has been updated")).toBe("base-moved")
    expect(classifyGhFailure("", "Branch is out-of-date with the base branch")).toBe("base-moved")
  })

  it("classifyGhFailure maps protection patterns to protection-conflict", () => {
    expect(classifyGhFailure("", "Protected branch update failed")).toBe("protection-conflict")
    expect(classifyGhFailure("", "Required status check \"build\" is expected.")).toBe("protection-conflict")
    expect(classifyGhFailure("", "At least 1 approving review is required")).toBe("protection-conflict")
  })

  it("classifyGhFailure maps PR-state patterns to pr-state-conflict", () => {
    expect(classifyGhFailure("", "Pull request is in a closed state")).toBe("pr-state-conflict")
    expect(classifyGhFailure("", "Pull request is in a merged state")).toBe("pr-state-conflict")
  })

  it("classifyGhFailure maps transient patterns to retry-safe", () => {
    expect(classifyGhFailure("", "API rate limit exceeded")).toBe("retry-safe")
    expect(classifyGhFailure("", "Could not resolve host github.com")).toBe("retry-safe")
    expect(classifyGhFailure("", "HTTP 502 Bad Gateway")).toBe("retry-safe")
    expect(classifyGhFailure("", "Connection reset by peer")).toBe("retry-safe")
  })

  it("classifyGhFailure defaults to retry-safe for unknown stderr", () => {
    expect(classifyGhFailure("", "Some unexpected text")).toBe("retry-safe")
    expect(classifyGhFailure("mysterious", "")).toBe("retry-safe")
  })

  it("classifyPushFailure mirrors classifyGhFailure", () => {
    expect(classifyPushFailure("", "Everything up-to-date")).toBe("retry-safe")
    expect(classifyPushFailure("", "Bad credentials")).toBe("config-error")
    expect(classifyPushFailure("", "non-fast-forward")).toBe("base-moved")
    expect(classifyPushFailure("", "Protected branch update failed")).toBe("protection-conflict")
  })
})

describe("mohist/publish-via-pr pattern predicates", () => {
  it("looksLikeBaseMoved", () => {
    expect(looksLikeBaseMoved("not mergeable")).toBe(true)
    expect(looksLikeBaseMoved("Merge conflict in x.md")).toBe(true)
    expect(looksLikeBaseMoved("Branch is out-of-date with the base branch")).toBe(true)
    expect(looksLikeBaseMoved("non-fast-forward")).toBe(true)
    expect(looksLikeBaseMoved("ok")).toBe(false)
  })

  it("looksLikeProtectionConflict", () => {
    expect(looksLikeProtectionConflict("protected branch")).toBe(true)
    expect(looksLikeProtectionConflict("required status check")).toBe(true)
    expect(looksLikeProtectionConflict("review required")).toBe(true)
    expect(looksLikeProtectionConflict("branch protection")).toBe(true)
    expect(looksLikeProtectionConflict("ok")).toBe(false)
  })

  it("looksLikePrStateConflict", () => {
    expect(looksLikePrStateConflict("pull request is in a closed state")).toBe(true)
    expect(looksLikePrStateConflict("pull request is in a merged state")).toBe(true)
    expect(looksLikePrStateConflict("ok")).toBe(false)
  })

  it("looksLikeAuthFailure", () => {
    expect(looksLikeAuthFailure("not logged in")).toBe(true)
    expect(looksLikeAuthFailure("bad credentials")).toBe(true)
    expect(looksLikeAuthFailure("must be logged in")).toBe(true)
    expect(looksLikeAuthFailure("ok")).toBe(false)
  })

  it("looksLikeRetrySafe", () => {
    expect(looksLikeRetrySafe("rate limit exceeded")).toBe(true)
    expect(looksLikeRetrySafe("could not resolve host")).toBe(true)
    expect(looksLikeRetrySafe("HTTP 502")).toBe(true)
    expect(looksLikeRetrySafe("ok")).toBe(false)
  })
})

describe("mohist/publish-via-pr JSON parsers", () => {
  it("parsePrList handles empty / non-JSON / array", () => {
    expect(parsePrList("")).toEqual([])
    expect(parsePrList("not json")).toEqual([])
    expect(parsePrList("[]")).toEqual([])
    expect(parsePrList('[{"number":3,"url":"https://x/pull/3"},{"number":4,"url":"https://x/pull/4"}]')).toEqual([
      { number: 3, url: "https://x/pull/3" },
      { number: 4, url: "https://x/pull/4" },
    ])
  })

  it("parsePrList skips malformed entries", () => {
    expect(parsePrList('[{"number":"3","url":"x"},{"number":5}]')).toEqual([])
    expect(parsePrList('[{"number":1,"url":"https://x/pull/1"},{"foo":"bar"}]')).toEqual([
      { number: 1, url: "https://x/pull/1" },
    ])
  })

  it("parsePrView returns state / mergeCommit / url", () => {
    expect(parsePrView(JSON.stringify({ state: "MERGED", url: "u", mergeCommit: { oid: "abc" } }))).toEqual({
      state: "MERGED",
      url: "u",
      mergeCommit: { oid: "abc" },
    })
    expect(parsePrView(JSON.stringify({ state: "OPEN" }))).toEqual({
      state: "OPEN",
      url: undefined,
      mergeCommit: null,
    })
    expect(parsePrView("not json")).toBeNull()
    expect(parsePrView("")).toBeNull()
  })

  it("extractPrNumberFromUrl parses PR URLs", () => {
    expect(extractPrNumberFromUrl("https://github.com/o/r/pull/42")).toBe(42)
    expect(extractPrNumberFromUrl("https://api.github.com/repos/o/r/pulls/99")).toBeNull()
    expect(extractPrNumberFromUrl("not a url")).toBeNull()
  })
})
