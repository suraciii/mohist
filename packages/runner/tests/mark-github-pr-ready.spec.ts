import { afterEach, describe, expect, it } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import {
  markGitHubPrReadyAction,
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "../src/actions/github-pr.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string; status?: "timeout"; timeoutMs?: number }
type GhCall = { command: string; timeoutMs: number | undefined }

const WORKSPACE_PATH = "/workspace"
const ghCalls: GhCall[] = []

afterEach(() => {
  setGitHubPrGitRunnerForTest(null)
  setGitHubPrGhRunnerForTest(null)
  ghCalls.length = 0
})

function ghOk(stdout: string, stderr = ""): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

function ghFail(stderr: string, stdout = "", exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

function context(withOverrides: JsonObject = {}, variables: JsonObject = {}): ActionContext {
  return {
    workflowRunId: "wr-ready-1",
    workId: "mark-pr-ready",
    workType: "task",
    stage: "check",
    title: "Mark GitHub PR ready for review",
    uses: "mohist/mark-github-pr-ready",
    with: withOverrides,
    variables: {
      project: { id: "proj_1", path: WORKSPACE_PATH },
      issue: { title: "Use GitHub PR workflow", body: "body", number: 248 },
      repository: { gitUrl: "https://example.com/repo.git", baseBranch: "master" },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-ready-1" },
      vars: { github: { pr: { number: 42, url: "https://github.com/example/repo/pull/42" } } },
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

function installGit(respond: () => never) {
  setGitHubPrGitRunnerForTest(async () => await respond())
}

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setGitHubPrGhRunnerForTest(async (cmd, args, cwd, _signal, _env, options) => {
    ghCalls.push({ command: [cmd, ...args].join(" "), timeoutMs: options?.timeoutMs })
    return await respond(cmd, args, cwd)
  })
}

describe("mohist/mark-github-pr-ready registry", () => {
  it("registers mark-github-pr-ready under its new id", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/mark-github-pr-ready")).toBe(markGitHubPrReadyAction)
  })
})

describe("mohist/mark-github-pr-ready action", () => {
  it("requires prNumber and reports config-error when missing", async () => {
    installGh(() => ghOk("never called"))

    const result = await markGitHubPrReadyAction(context({}))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("requires prNumber")
  })

  it("is idempotent: a PR already marked READY returns success without gh pr ready", async () => {
    const ghCalls: string[] = []
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "OPEN", isDraft: false, url: "https://github.com/example/repo/pull/42" }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: "mark-github-pr-ready",
      status: "completed",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      state: "READY",
      previousState: "READY",
      transitioned: false,
    })
    expect(output.output).toContain("already READY")
    expect(ghCalls.some((call) => call === "gh pr ready 42")).toBe(false)
  })

  it("forwards gh command output to the task log sink", async () => {
    const writes: Array<{ source: string; text: string }> = []
    installGit(() => { throw new Error("git should not be called") })
    setGitHubPrGhRunnerForTest(async (cmd, args, _cwd, _signal, _env, options) => {
      const full = [cmd, ...args].join(" ")
      options?.onLine?.(`captured ${full}`)
      if (full === "gh --version") return ghOk("gh version 2.0.0\n")
      if (full === "gh auth status") return ghOk("Logged in\n")
      if (full === "gh pr view 42 --json state,isDraft,url") return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/acme/repo/pull/42" }))
      if (full === "gh pr ready 42") return ghOk("ready\n")
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await markGitHubPrReadyAction(withLog(context({ prNumber: 42 }), writes))

    expect(result.error).toBeUndefined()
    expect(writes.some((write) => write.source === "action:mark-github-pr-ready" && write.text.includes("gh pr ready 42"))).toBe(true)
  })

  it("transitions a draft PR to READY when isDraft is true", async () => {
    const ghCalls: string[] = []
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/example/repo/pull/42" }))
        case "gh pr ready 42":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    const output = JSON.parse(result.output ?? "{}")

    expect(result.error).toBeUndefined()
    expect(output).toMatchObject({
      kind: "mark-github-pr-ready",
      status: "completed",
      prNumber: 42,
      prUrl: "https://github.com/example/repo/pull/42",
      state: "READY",
      previousState: "DRAFT",
      transitioned: true,
    })
    expect(ghCalls).toContain("gh pr ready 42")
  })

  it("scopes ready delivery to the authoritative Issue repository", async () => {
    const prArguments: string[][] = []
    installGit(() => { throw new Error("git should not be called") })
    installGh((_cmd, args) => {
      if (args[0] === "--version" || args.join(" ") === "auth status") return ghOk("ok\n")
      if (args[0] === "pr") prArguments.push(args)
      if (args.join(" ").startsWith("pr view ")) return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/acme/repo/pull/42" }))
      if (args.join(" ").startsWith("pr ready ")) return ghOk("ready\n")
      return ghFail(`unexpected gh call: ${args.join(" ")}`)
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }, authoritativeRepository()))

    expect(result.error).toBeUndefined()
    expect(prArguments).toHaveLength(2)
    expect(prArguments.every((args) => args.slice(-2).join(" ") === "--repo github.com/acme/repo")).toBe(true)
  })

  it("fails closed when the authoritative Issue repository URL is unparseable", async () => {
    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }, authoritativeRepository("not a Git URL")))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("authoritative GitHub repository URL")
  })

  it("does not call git push or update title/body — the action is a state transition only", async () => {
    const ghCalls: string[] = []
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/example/repo/pull/42" }))
        case "gh pr ready 42":
          return ghOk("ok\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    await markGitHubPrReadyAction(context({ prNumber: 42 }))

    const forbiddenStarts = ["gh pr edit", "gh pr create", "gh pr merge", "gh pr close", "gh pr reopen"]
    for (const call of ghCalls) {
      for (const forbidden of forbiddenStarts) {
        expect(call.startsWith(forbidden)).toBe(false)
      }
    }
    expect(ghCalls).toContain("gh pr ready 42")
    expect(ghCalls).toContain("gh pr view 42 --json state,isDraft,url")
  })

  it("reports config-error when the gh CLI precheck fails", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full === "gh --version") return ghFail("gh: command not found", "", 127)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "config-error" })
    expect(result.error?.message).toContain("gh CLI is not installed")
  })

  it("returns errorCode pr-state-conflict if the PR is closed", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "CLOSED", isDraft: true, url: "https://github.com/example/repo/pull/42" }))
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "pr-state-conflict" })
    expect(result.error?.message).toContain("in state CLOSED")
  })

  it("classifies gh pr ready failures via the shared classifier", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/example/repo/pull/42" }))
        case "gh pr ready 42":
          return ghFail("fatal: could not resolve host api.github.com")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "retry-safe" })
    expect(result.error?.message).toContain("gh pr ready 42 failed")
  })

  it("falls back to errorCode retry-safe when gh pr view returns unparseable JSON", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk("not json")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "retry-safe" })
  })

  it("NetworkGhCalls_AllReceiveTimeoutMs", async () => {
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/example/repo/pull/42" }))
        case "gh pr ready 42":
          return ghOk("https://github.com/example/repo/pull/42\n")
        default:
          return ghFail(`unexpected gh call: ${full}`)
      }
    })

    await markGitHubPrReadyAction(context({ prNumber: 42 }))

    for (const command of ["gh --version", "gh auth status", "gh pr view 42 --json state,isDraft,url", "gh pr ready 42"]) {
      const call = ghCalls.find((c) => c.command === command)
      expect(call?.timeoutMs, `gh call ${command} missing timeoutMs`).toBe(NETWORK_COMMAND_TIMEOUT_MS)
    }
  })

  it("GhPrReadyTimeout_ClassifiesAsRetrySafeAndSurfacesDuration", async () => {
    installGit(() => { throw new Error("git should not be called") })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      switch (full) {
        case "gh --version":
        case "gh auth status":
          return ghOk("ok\n")
        case "gh pr view 42 --json state,isDraft,url":
          return ghOk(JSON.stringify({ state: "OPEN", isDraft: true, url: "https://github.com/example/repo/pull/42" }))
        case "gh pr ready 42":
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

    const result = await markGitHubPrReadyAction(context({ prNumber: 42 }))
    expect(result.error).toBeDefined()
    expect(result.error).toMatchObject({ code: "timeout" })
    expect(result.error?.message).toContain("timed out")
  })
})

function authoritativeRepository(gitUrl = "https://github.com/acme/repo.git"): JsonObject {
  return {
    repository: {
      name: "repo",
      gitUrl,
      baseBranch: "master",
    },
  }
}
