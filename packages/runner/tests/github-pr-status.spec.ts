import { afterEach, describe, expect, it } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import { NETWORK_COMMAND_TIMEOUT_MS } from "../src/actions/git.js"
import {
  __testing,
  githubPrStatusAction,
  parseGitHubPrStatusExpectation,
  setGitHubPrStatusGhRunnerForTest,
} from "../src/actions/github-pr-status.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string; status?: "timeout"; timeoutMs?: number }
type GhCall = { command: string; timeoutMs: number | undefined }

const WORKSPACE_PATH = "/workspace"
const ghCalls: GhCall[] = []

afterEach(() => {
  setGitHubPrStatusGhRunnerForTest(null)
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
    workflowRunId: "wr-gh-status-1",
    workId: "github-pr-status",
    workType: "task",
    stage: "check",
    title: "GitHub PR status",
    uses: "mohist/github-pr-status",
    with: withOverrides,
    variables: {
      project: { id: "proj_1", path: WORKSPACE_PATH },
      issue: { title: "Use GitHub PR workflow", body: "Open, review, and merge a GitHub PR.", number: 248 },
      repository: {
        gitUrl: "https://example.com/repo.git",
        baseBranch: "master",
      },
      workspace: { path: WORKSPACE_PATH, branch: "mohist/run-wr-gh-status-1" },
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

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setGitHubPrStatusGhRunnerForTest(async (cmd, args, cwd, _signal, _env, options) => {
    ghCalls.push({ command: [cmd, ...args].join(" "), timeoutMs: options?.timeoutMs })
    return await respond(cmd, args, cwd)
  })
}

const PR_VIEW_OPEN = JSON.stringify({
  url: "https://github.com/acme/repo/pull/42",
  state: "OPEN",
  isDraft: false,
})

const PR_VIEW_DRAFT = JSON.stringify({
  url: "https://github.com/acme/repo/pull/42",
  state: "OPEN",
  isDraft: true,
})

const PR_VIEW_MERGED = JSON.stringify({
  url: "https://github.com/acme/repo/pull/42",
  state: "MERGED",
  isDraft: false,
})

describe("mohist/github-pr-status registry", () => {
  it("registers github-pr-status in the default registry", () => {
    const registry = createDefaultRegistry()
    expect(registry.resolve("mohist/github-pr-status")).toBe(githubPrStatusAction)
  })
})

describe("mohist/github-pr-status action", () => {
  it("returns success when the PR is OPEN and not draft (default ready+open expectations)", async () => {
    const ghCalls: string[] = []
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.error).toBeUndefined()
    const parsed = JSON.parse(result.output!)
    expect(parsed.kind).toBe("github-pr-status")
    expect(parsed.status).toBe("verified")
    expect(parsed.prNumber).toBe(42)
    expect(parsed.prUrl).toBe("https://github.com/acme/repo/pull/42")
    expect(parsed.prState).toBe("OPEN")
    expect(parsed.isDraft).toBe(false)
    expect(parsed.expectations).toEqual(["open", "ready"])
    expect(parsed.missing).toEqual([])
    expect(ghCalls).toContain("gh pr view 42 --json url,state,isDraft")
  })

  it("scopes status delivery to the authoritative Issue repository", async () => {
    const commands: string[] = []
    installGh((cmd, args) => {
      commands.push([cmd, ...args].join(" "))
      if (args.join(" ") === "pr view 42 --json url,state,isDraft --repo github.com/acme/repo") return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${[cmd, ...args].join(" ")}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }, authoritativeRepository()))

    expect(result.error).toBeUndefined()
    expect(commands).toEqual(["gh pr view 42 --json url,state,isDraft --repo github.com/acme/repo"])
  })

  it("fails closed when the authoritative Issue repository URL is unparseable", async () => {
    const result = await githubPrStatusAction(context({ prNumber: 42 }, authoritativeRepository("not a Git URL")))
    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("authoritative GitHub repository URL")
  })

  it("forwards gh command output to the task log sink", async () => {
    const writes: Array<{ source: string; text: string }> = []
    setGitHubPrStatusGhRunnerForTest(async (cmd, args, _cwd, _signal, _env, options) => {
      const full = [cmd, ...args].join(" ")
      options?.onLine?.(`captured ${full}`)
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(withLog(context({ prNumber: 42 }), writes))

    expect(result.error).toBeUndefined()
    expect(writes).toEqual([{ source: "action:github-pr-status", text: "captured gh pr view 42 --json url,state,isDraft" }])
  })

  it("rejects a draft PR by default", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_DRAFT)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.error).toBeDefined()
    expect(result.error?.code).toBe("pr-status-failed")
  })

  it("rejects a non-open PR by default", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_MERGED)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.error).toBeDefined()
    expect(result.error?.code).toBe("pr-status-failed")
  })

  it("fails with expect=merged when the PR state is OPEN", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full === "gh pr view 42 --json url,state") return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "merged" }))

    expect(result.error).toBeDefined()
    expect(result.error?.code).toBe("pr-status-failed")
  })

  it("passes expect=merged when the PR state is MERGED", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_MERGED)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "merged" }))

    expect(result.error).toBeUndefined()
    const parsed = JSON.parse(result.output!)
    expect(parsed.status).toBe("verified")
    expect(parsed.missing).toEqual([])
  })

  it("rejects a draft PR when expect=ready is set", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_DRAFT)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "ready" }))

    expect(result.error).toBeDefined()
    expect(result.error?.code).toBe("pr-status-failed")
  })

  it("resolves prNumber from vars.github.pr.number when omitted from with", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 7")) return ghOk(PR_VIEW_OPEN.replace("42", "7"))
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({}, {
      github: { pr: { number: 7, url: "https://github.com/acme/repo/pull/7" } },
    }))

    expect(result.error).toBeUndefined()
    const parsed = JSON.parse(result.output!)
    expect(parsed.prNumber).toBe(7)
  })

  it("returns failure with a clear message when prNumber is missing", async () => {
    const result = await githubPrStatusAction(context({}))

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("prNumber")
  })

  it("returns failure when gh pr view fails", async () => {
    installGh(() => ghFail("gh: not found"))

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("gh pr view 42 failed")
  })

  it("returns failure when gh pr view returns unparseable JSON", async () => {
    installGh(() => ghOk("not-json"))

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.error).toBeDefined()
    expect(result.error?.message).toContain("unparseable JSON")
  })

  it("ignores unknown expectation tokens", async () => {
    expect(parseGitHubPrStatusExpectation("merged, foo")).toEqual(["merged"])
    expect(parseGitHubPrStatusExpectation(null)).toEqual(["open", "ready"])
    expect(parseGitHubPrStatusExpectation("")).toEqual(["open", "ready"])
  })

  it("requests only fields needed by each expectation set", () => {
    expect(__testing.buildPrViewFields(["open", "ready"])).toEqual(["url", "state", "isDraft"])
    expect(__testing.buildPrViewFields(["merged"])).toEqual(["url", "state"])
  })

  it("NetworkGhPrView_ReceivesTimeoutMs", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    await githubPrStatusAction(context({ prNumber: 42 }))

    const view = ghCalls.find((c) => c.command.startsWith("gh pr view 42"))
    expect(view?.timeoutMs).toBe(NETWORK_COMMAND_TIMEOUT_MS)
  })

  it("GhPrViewTimeout_SurfacesStepNameAndDuration", async () => {
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) {
        return {
          exitCode: 124,
          stdout: "",
          stderr: `Command timed out after ${NETWORK_COMMAND_TIMEOUT_MS / 1000}s\n`,
          status: "timeout" as const,
          timeoutMs: NETWORK_COMMAND_TIMEOUT_MS,
        }
      }
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))
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
