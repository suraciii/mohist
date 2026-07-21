import { afterEach, describe, expect, it } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { setIssueFieldCommandRunnerForTest } from "../src/actions/issue-fields.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import {
  createGitHubPrAction,
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "../src/actions/github-pr.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string; status?: "timeout"; timeoutMs?: number }
type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string; status?: "timeout"; timeoutMs?: number }
type GitCall = { command: string; timeoutMs: number | undefined }
type GhCall = { command: string; timeoutMs: number | undefined }

const WORKSPACE_PATH = "/workspace"
const PROJECT_PATH = "/project"

afterEach(() => {
  setGitHubPrGitRunnerForTest(null)
  setGitHubPrGhRunnerForTest(null)
  setIssueFieldCommandRunnerForTest(null)
  gitCalls.length = 0
  ghCalls.length = 0
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
    writeVars: async () => {},
  }
}

function withLog(ctx: ActionContext, writes: Array<{ source: string; text: string }>): ActionContext {
  return {
    ...ctx,
    log: { write: (source: string, text: string) => { writes.push({ source, text }); return writes.length } } as never,
  }
}

const gitCalls: GitCall[] = []
const ghCalls: GhCall[] = []

function installGit(respond: (workDir: string, args: string[], signal: AbortSignal) => GitResponse | Promise<GitResponse>) {
  setGitHubPrGitRunnerForTest(async (workDir, args, signal, options) => {
    const recorded: GitCall = { command: args.join(" "), timeoutMs: options?.timeoutMs }
    gitCalls.push(recorded)
    return await respond(workDir, args, signal)
  })
}

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setGitHubPrGhRunnerForTest(async (cmd, args, cwd, _signal, _env, options) => {
    const recorded: GhCall = { command: [cmd, ...args].join(" "), timeoutMs: options?.timeoutMs }
    ghCalls.push(recorded)
    return await respond(cmd, args, cwd)
  })
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

function authoritativeRepository(gitUrl = "https://github.com/acme/repo.git"): JsonObject {
  return {
    repository: {
      name: "repo",
      gitUrl,
      baseBranch: "master",
    },
  }
}

describe("mohist/create-github-pr registry", () => {
  it("registers create-github-pr and exposes it under the new id only", () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve("mohist/create-github-pr")
    expect(resolved.kind).toBe("definition")
    if (resolved.kind === "definition") {
      expect(resolved.definition.manifest.name).toBe("mohist/create-github-pr")
    }
    expect(registry.resolve("mohist/create-pull-request").kind).toBe("unknown")
    expect(registry.resolve("mohist/publish-via-pr").kind).toBe("unknown")
  })
})

describe("mohist/create-github-pr action", () => {
  it("opens a draft PR from an already-published workflow branch", async () => {
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
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(gitCalls).toEqual([])
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
      draft: true,
      output: "https://github.com/example/repo/pull/42",
    })
  })

  it("scopes GitHub PR delivery to the authoritative Issue repository", async () => {
    const prArguments: string[][] = []
    installGit((_workDir, args) => {
      if (args.join(" ") === "fetch origin master") return ok("")
      if (args.join(" ") === "rev-parse origin/master") return ok("base-sha\n")
      if (args.join(" ") === "push --force-with-lease origin mohist/run-wr-gh-pr-1") return ok("")
      return fail(`unexpected git call: ${args.join(" ")}`)
    })
    installGh((_cmd, args) => {
      if (args[0] === "--version" || args.join(" ") === "auth status") return ghOk("ok\n")
      if (args[0] === "pr") prArguments.push(args)
      if (args.join(" ").startsWith("pr list ")) return ghOk("[]\n")
      if (args.join(" ").startsWith("pr create ")) return ghOk("https://github.com/acme/repo/pull/42\n")
      return ghFail(`unexpected gh call: ${args.join(" ")}`)
    })

    const result = await createGitHubPrAction(context({ title: "Issue title", body: "Issue body" }, authoritativeRepository()))

    expect(result.error).toBeUndefined()
    expect(prArguments).toHaveLength(2)
    expect(prArguments.every((args) => args.slice(-2).join(" ") === "--repo github.com/acme/repo")).toBe(true)
  })

  it("fails closed when the authoritative Issue repository URL is unparseable", async () => {
    const result = await createGitHubPrAction(context({ title: "Issue title", body: "Issue body" }, authoritativeRepository("not a Git URL")))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("authoritative GitHub repository URL")
  })

  it("forwards gh command output to the task log sink", async () => {
    const writes: Array<{ source: string; text: string }> = []
    installMoIssueShow()
    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      if (cmd === "fetch origin master") return ok("base fetched\n")
      if (cmd === "rev-parse origin/master") return ok("base-sha-1\n")
      if (cmd === "push --force-with-lease origin mohist/run-wr-gh-pr-1") return ok("pushed\n")
      return fail(`unexpected git call: ${cmd}`)
    })
    setGitHubPrGhRunnerForTest(async (cmd, args, cwd, _signal, _env, options) => {
      const full = [cmd, ...args].join(" ")
      options?.onLine?.(`captured ${full}`)
      if (full === "gh --version") return ghOk("gh version 2.0.0\n")
      if (full === "gh auth status") return ghOk("Logged in\n")
      if (full.startsWith("gh pr list")) return ghOk("[]\n")
      if (full.startsWith("gh pr create")) return ghOk("https://github.com/acme/repo/pull/42\n")
      return ghFail(`unexpected gh call in ${cwd}: ${full}`)
    })

    const result = await createGitHubPrAction(withLog(context({ target: "master" }), writes))

    expect(result.error).toBeUndefined()
    expect(writes.some((write) => write.source === "action:create-github-pr" && write.text.includes("gh pr create"))).toBe(true)
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
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls).toContain("gh pr edit 7 --title Fresh issue title --body Fresh issue body")
    expect(ghCalls.some((call) => call.startsWith("gh pr create "))).toBe(false)
    expect(output).toMatchObject({
      kind: "create-github-pr",
      status: "completed",
      operation: "reused",
      prNumber: 7,
      prUrl: "https://github.com/example/repo/pull/7",
      draft: true,
    })
  })

  it("binds every GitHub invocation to the workspace path even when project.path differs", async () => {
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
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls.map((call) => call.cwd)).toEqual([WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH, WORKSPACE_PATH])
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
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("Unsupported titleFrom source 'issue.summary'")
  })

  it("does not invoke Git when GitHub creates the PR", async () => {
    installGit(() => fail("create-github-pr must not invoke git"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full === "gh --version" || full === "gh auth status") return ghOk("ok\n")
      if (full.startsWith("gh pr list ")) return ghOk("[]\n")
      if (full.startsWith("gh pr create ")) return ghOk("https://github.com/example/repo/pull/42\n")
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
      target: "master",
      remote: "origin",
      title: "Issue title",
      body: "Issue body",
    }))
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(gitCalls).toEqual([])
    expect(output.operation).toBe("created")
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
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("gh CLI is not installed")
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
    const output = result.output as Record<string, unknown>

    expect(result.error).toBeUndefined()
    expect(ghCalls.some((call) => call.startsWith("gh pr create ") && call.endsWith("--draft"))).toBe(false)
    expect(ghCalls.some((call) => call.startsWith("gh pr create "))).toBe(true)
    expect(output).toMatchObject({
      draft: false,
      operation: "created",
    })
  })

  it("NetworkGitHubCommands_AllReceiveTimeoutMs", async () => {
    installMoIssueShow()
    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      switch (cmd) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${cmd}`)
      }
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.0.0\n")
        case "gh auth status":
          return ghOk("Logged in\n")
        case "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    await createGitHubPrAction(context({
      source: "mohist/run-wr-gh-pr-1",
      target: "master",
      remote: "origin",
      titleFrom: "issue.title",
      bodyFrom: "issue.body",
    }))

    for (const command of [
      "gh --version",
      "gh auth status",
      "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft",
      "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft",
    ]) {
      const call = ghCalls.find((c) => c.command === command)
      expect(call?.timeoutMs, `gh call ${command} missing timeoutMs`).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    }

    expect(gitCalls).toEqual([])
  })

  it("GhPrCreateTimeout_ClassifiesAsRetrySafeAndSurfacesDuration", async () => {
    installMoIssueShow()
    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      switch (cmd) {
        case "fetch origin master":
          return ok("")
        case "rev-parse origin/master":
          return ok("base-sha-1\n")
        case "push --force-with-lease origin mohist/run-wr-gh-pr-1":
          return ok("")
        default:
          return fail(`unexpected git call: ${cmd}`)
      }
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
          return ghOk("gh version 2.0.0\n")
        case "gh auth status":
          return ghOk("Logged in\n")
        case "gh pr list --head mohist/run-wr-gh-pr-1 --base master --state open --json number,url,isDraft":
          return ghOk("[]\n")
        case "gh pr create --head mohist/run-wr-gh-pr-1 --base master --title Use GitHub PR workflow --body Open, review, and merge a GitHub PR. --draft":
          return {
            exitCode: 124,
            stdout: "",
            stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
            status: "timeout" as const,
            timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
          }
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
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "timeout" })
    expect(result.error?.message).toContain("timed out")
  })

})
