import { afterEach, describe, expect, it } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { setIssueFieldCommandRunnerForTest } from "../src/actions/issue-fields.js"
import {
  createGitHubPrAction,
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "../src/actions/github-pr.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }
type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

const WORKSPACE_PATH = "/workspace"
const PROJECT_PATH = "/project"

afterEach(() => {
  setGitHubPrGitRunnerForTest(null)
  setGitHubPrGhRunnerForTest(null)
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
    workflowRunId: "wr-gh-pr-1",
    workId: "open-draft-pr",
    workType: "task",
    stage: "plan",
    title: "Open or reuse GitHub draft PR",
    uses: "mohist/create-github-pr",
    with: withOverrides,
    variables: {
      project: { id: "proj_1", path: WORKSPACE_PATH },
      issue: { title: "Use GitHub PR workflow", body: "Open, review, and merge a GitHub PR.", number: 248 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
      },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-gh-pr-1" },
      ...variables,
    },
    workDir: WORKSPACE_PATH,
    projectId: "proj_1",
    issueNumber: 248,
    signal: new AbortController().signal,
  }
}

function installGit(respond: (workDir: string, args: string[], signal: AbortSignal) => GitResponse | Promise<GitResponse>) {
  setGitHubPrGitRunnerForTest(async (workDir, args, signal) => await respond(workDir, args, signal))
}

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setGitHubPrGhRunnerForTest(async (cmd, args, cwd, _signal) => await respond(cmd, args, cwd))
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

describe("mohist/create-github-pr registry", () => {
  it("registers create-github-pr and exposes it under the new id only", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/create-github-pr")).toBe(createGitHubPrAction)
    expect(registry.resolve("mohist/create-pull-request")).toBeUndefined()
    expect(registry.resolve("mohist/publish-via-pr")).toBeUndefined()
  })
})

describe("mohist/create-github-pr action", () => {
  it("pushes the workflow branch, opens a draft PR, and returns prNumber/prUrl for setVars", async () => {
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
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
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
        case "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
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
      "push --force-with-lease origin mohist/run-wr-gh-pr-1",
    ])
    expect(ghCalls).toEqual([
      "gh --version",
      "gh auth status",
      "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft",
      "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft",
    ])
    expect(moCalls).toEqual([
      "mo issue show 248 --project-id proj_1 --output json",
    ])
    expect(output).toMatchObject({
      kind: "create-github-pr",
      status: "completed",
      branch: "mohist/run-wr-gh-pr-1",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      operation: "created",
      errorCode: null,
      message: null,
      baseSha: "base-sha-1",
      pushed: true,
      draft: true,
    })
  })

  it("reuses an existing open PR without mutating title/body when gh pr list returns a match", async () => {
    const ghCalls: string[] = []
    installMoIssueShow("Fresh issue title", "Fresh issue body")
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
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
        case "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft":
          return ghOk(JSON.stringify([{ number: 7, url: "https://github.com/example/repo/pull/7", isDraft: true }]))
        case "gh pr edit 7 --title Fresh issue title --body Fresh issue body":
          return ghOk("")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
      target: "master",
      remote: "origin",
      titleFrom: "issue.title",
      bodyFrom: "issue.body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls).toContain("gh pr edit 7 --title Fresh issue title --body Fresh issue body")
    expect(ghCalls.some((call) => call.startsWith("gh pr create "))).toBe(false)
    expect(output).toMatchObject({
      kind: "create-github-pr",
      status: "completed",
      operation: "reused",
      prNumber: 7,
      prUrl: "https://github.com/example/repo/pull/7",
      pushed: true,
      draft: true,
    })
  })

  it("binds every git/gh invocation to the workspace path even when project.path differs", async () => {
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
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
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
        case "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Issue title --body Issue body --draft":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${command}`)
      }
    })

    const result = await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
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

    const result = await createGitHubPrAction(context({
      titleFrom: "issue.summary",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "create-github-pr",
      errorCode: "config-error",
      prNumber: null,
      prUrl: null,
      pushed: false,
    })
    expect(output.message).toContain("Unsupported titleFrom source 'issue.summary'")
  })

  it("reports base-moved when the force-with-lease push is rejected as non-fast-forward", async () => {
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
          return fail("To https://example.com/repo.git\n ! [rejected]        mohist/run-wr-gh-pr-1 -> mohist/run-wr-gh-pr-1 (non-fast-forward)\nerror: failed to push some refs")
        default:
          return fail(`unexpected git call: ${args.join(" ")}`)
      }
    })
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

    const result = await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
      target: "master",
      remote: "origin",
      title: "Issue title",
      body: "Issue body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "create-github-pr",
      status: "failed",
      errorCode: "base-moved",
      branch: "mohist/run-wr-gh-pr-1",
      pushed: false,
    })
  })

  it("reports config-error when the gh CLI precheck fails", async () => {
    installGit(() => fail("git should not be called"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full === "gh --version") {
        return ghFail("gh: command not found", "", 127)
      }
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await createGitHubPrAction(context({
      title: "Issue title",
      body: "Issue body",
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("failure")
    expect(output).toMatchObject({
      kind: "create-github-pr",
      errorCode: "config-error",
    })
    expect(output.message).toContain("gh CLI is not installed")
  })

  it("does not pass --draft when draft is explicitly false", async () => {
    const ghCalls: string[] = []
    installGit((_workDir, args) => {
      switch (args.join(" ")) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
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
        case "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR.":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
      target: "master",
      remote: "origin",
      title: "Use GitHub PR workflow",
      body: "Open, review, and merge a GitHub PR.",
      draft: false,
    }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.status).toBe("success")
    expect(ghCalls.some((call) => call.startsWith("gh pr create ") && call.endsWith("--draft"))).toBe(false)
    expect(ghCalls.some((call) => call.startsWith("gh pr create "))).toBe(true)
    expect(output).toMatchObject({
      draft: false,
      operation: "created",
    })
  })
})
