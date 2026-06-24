import { afterEach, describe, expect, it } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { setIssueFieldCommandRunnerForTest } from "../src/actions/issue-fields.js"
import {
  createPullRequestAction,
  mergePullRequestAction,
  setPullRequestGhRunnerForTest,
  setPullRequestGitRunnerForTest,
} from "../src/actions/pull-request.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }
type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

const WORKSPACE_PATH = "/workspace"
const PROJECT_PATH = "/project"

afterEach(() => {
  setPullRequestGitRunnerForTest(null)
  setPullRequestGhRunnerForTest(null)
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

function context(uses: string, withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-pr-1",
    workId: uses.endsWith("create-pull-request") ? "integrate:open-pr.1" : "integrate:merge-pr.1",
    workType: "task",
    stage: "integrate",
    title: uses.endsWith("create-pull-request") ? "Open or update GitHub PR" : "Merge GitHub PR",
    uses,
    with: withOverrides,
    variables: {
      project: { id: "proj_1", path: WORKSPACE_PATH },
      issue: { title: "stale variable title", body: "stale variable body", number: 248 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
      },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-pr-1" },
      vars: { github: { pr: { number: 42, url: "https://github.com/example/repo/pull/42" } } },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    projectId: "proj_1",
    issueNumber: 248,
    signal: new AbortController().signal,
  }
}

function installGit(respond: (workDir: string, args: string[]) => GitResponse | Promise<GitResponse>) {
  setPullRequestGitRunnerForTest(respond)
}

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setPullRequestGhRunnerForTest(async (cmd, args, cwd, _signal) => await respond(cmd, args, cwd))
}

function installMoIssueShow(title = "Use GitHub PR workflow", body = "Open, review, and merge a GitHub PR.") {
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

describe("mohist/create-pull-request and mohist/merge-pull-request registry", () => {
  it("registers both split GitHub PR actions", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/create-pull-request")).toBe(createPullRequestAction)
    expect(registry.resolve("mohist/merge-pull-request")).toBe(mergePullRequestAction)
  })
})

describe("mohist/create-pull-request action", () => {
  it("pushes the workflow branch, creates a PR, and returns setVars-friendly metadata", async () => {
    const gitCalls: string[] = []
    const ghCalls: string[] = []
    const moCalls = installMoIssueShow()

    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      gitCalls.push(cmd)
      switch (cmd) {
        case "fetch origin master":
          return ok("From https://example.com/repo.git\n")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("To https://example.com/repo.git\n")
        default:
          return fail(`unexpected git call: ${cmd}`)
      }
    })

    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.55.0\n")
        case "gh auth status":
          return ghOk("authenticated\n")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR.":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await createPullRequestAction(context("mohist/create-pull-request", {
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
      "gh pr create --head mohist/run-wr-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR.",
    ])
    expect(moCalls).toEqual([
      "mo issue show 248 --project-id proj_1 --output json",
    ])
    expect(output).toMatchObject({
      kind: "create-pull-request",
      status: "completed",
      branch: "mohist/run-wr-pr-1",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      operation: "created",
      errorCode: null,
      message: null,
      baseSha: "base-sha-1",
      pushed: true,
    })
  })

  it("updates an existing open PR title/body instead of leaving stale copy", async () => {
    const ghCalls: string[] = []
    installMoIssueShow("Fresh issue title", "Fresh issue body")
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk(JSON.stringify([{ number: 7, url: "https://github.com/example/repo/pull/7" }]))
        case "gh pr edit 7 --title Fresh issue title --body Fresh issue body":
          return ghOk("")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await createPullRequestAction(context("mohist/create-pull-request", {
      source: "mohist/run-wr-pr-1",
      target: "master",
      remote: "origin",
      titleFrom: "issue.title",
      bodyFrom: "issue.body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls).toContain("gh pr edit 7 --title Fresh issue title --body Fresh issue body")
    expect(output.operation).toBe("updated")
    expect(output.prNumber).toBe(7)
  })

  it("ProjectPathDiffers_UsesBoundWorkspacePath", async () => {
    const gitCalls: Array<{ workDir: string; command: string }> = []
    const ghCalls: Array<{ cwd: string; command: string }> = []

    installGit((workDir, args) => {
      const command = args.join(" ")
      gitCalls.push({ workDir, command })
      switch (command) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-pr-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${command}`)
      }
    })
    installGh((cmd, args, cwd) => {
      const command = [cmd, ...args].join(" ")
      ghCalls.push({ cwd, command })
      switch (command) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-pr-1 --base master --title Issue title --body Issue body":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${command}`)
      }
    })

    const result = await createPullRequestAction(context("mohist/create-pull-request", {
      source: "mohist/run-wr-pr-1",
      target: "master",
      remote: "origin",
      title: "Issue title",
      body: "Issue body",
    }, { project: { id: "proj_1", path: PROJECT_PATH } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(gitCalls.map((call) => call.workDir)).toEqual([WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH])
    expect(ghCalls.map((call) => call.cwd)).toEqual([WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH])
    expect(gitCalls.some((call) => call.workDir === PROJECT_PATH)).toBe(false)
    expect(ghCalls.some((call) => call.cwd === PROJECT_PATH)).toBe(false)
    expect(output.prNumber).toBe(42)
  })

  it("reports unsupported issue field sources as errorCode config-error", async () => {
    installGit(() => fail("git should not be called"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await createPullRequestAction(context("mohist/create-pull-request", {
      titleFrom: "issue.summary",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("config-error")
    expect(output.message).toContain("Unsupported titleFrom source 'issue.summary'")
  })
})

describe("mohist/merge-pull-request action", () => {
  it("merges a PR via squash with a subject resolved from the issue title", async () => {
    const ghCalls: string[] = []
    const moCalls = installMoIssueShow("Use GitHub PR workflow", "body")
    installGit(() => fail("git should not be called"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await mergePullRequestAction(context("mohist/merge-pull-request", {
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
      "gh pr view 42 --json state,mergeCommit,url,number",
      "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ",
      "gh pr view 42 --json state,mergeCommit,url",
    ])
    expect(output).toMatchObject({
      kind: "merge-pull-request",
      status: "completed",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      mergeCommitSha: "merge-sha-1",
      method: "squash",
      errorCode: null,
      message: null,
    })
  })

  it("can resolve an open PR from source/target when prNumber is omitted", async () => {
    installMoIssueShow("Use GitHub PR workflow", "body")
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr list --head mohist/run-wr-pr-1 --base master --state open --json number,url":
          return ghOk(JSON.stringify([{ number: 9, url: "https://github.com/example/repo/pull/9" }]))
        case "gh pr view 9 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "MERGED", number: 9, url: "https://github.com/example/repo/pull/9", mergeCommit: { oid: "merge-sha-9" } }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await mergePullRequestAction(context("mohist/merge-pull-request", {
      source: "mohist/run-wr-pr-1",
      target: "master",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(output.prNumber).toBe(9)
    expect(output.mergeCommitSha).toBe("merge-sha-9")
  })

  it("reports base-moved when GitHub says the PR is not mergeable", async () => {
    installMoIssueShow("Use GitHub PR workflow", "body")
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr merge 42 --squash --subject Use GitHub PR workflow --body ":
          return ghFail("GraphQL: Pull request is not mergeable")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await mergePullRequestAction(context("mohist/merge-pull-request", {
      prNumber: 42,
      method: "squash",
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "merge-pull-request",
      status: "failed",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      errorCode: "base-moved",
    })
    expect(output.message).toContain("gh pr merge 42 --squash failed")
  })

  it("reports a closed PR as pr-state-conflict", async () => {
    installMoIssueShow("Use GitHub PR workflow", "body")
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "CLOSED", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await mergePullRequestAction(context("mohist/merge-pull-request", {
      prNumber: 42,
      subjectFrom: "issue.title",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output.errorCode).toBe("pr-state-conflict")
    expect(output.message).toContain("PR #42 is closed")
  })

  it("ProjectPathDiffers_UsesBoundWorkspacePath", async () => {
    const ghCalls: Array<{ cwd: string; command: string }> = []
    installGit(() => fail("git should not be called"))
    installGh((cmd, args, cwd) => {
      const command = [cmd, ...args].join(" ")
      ghCalls.push({ cwd, command })
      switch (command) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,mergeCommit,url,number":
          return ghOk(JSON.stringify({ state: "OPEN", number: 42, url: "https://github.com/example/repo/pull/42", mergeCommit: null }))
        case "gh pr merge 42 --squash --subject Issue title --body ":
          return ghOk("Merged pull request #42\n")
        case "gh pr view 42 --json state,mergeCommit,url":
          return ghOk(JSON.stringify({ state: "MERGED", url: "https://github.com/example/repo/pull/42", mergeCommit: { oid: "merge-sha-1" } }))
        default:
          return ghFail(`unexpected gh call: ${command}`)
      }
    })

    const result = await mergePullRequestAction(context("mohist/merge-pull-request", {
      prNumber: 42,
      subject: "Issue title",
    }, { project: { id: "proj_1", path: PROJECT_PATH } }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls.map((call) => call.cwd)).toEqual([WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH])
    expect(ghCalls.some((call) => call.cwd === PROJECT_PATH)).toBe(false)
    expect(output.mergeCommitSha).toBe("merge-sha-1")
  })
})
