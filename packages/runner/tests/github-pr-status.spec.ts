import { afterEach, describe, expect, it } from "vitest"
import type { ActionContext, JsonObject } from "../src/core/types.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import {
  githubPrStatusAction,
  parseGitHubPrStatusExpectation,
  setGitHubPrStatusGhRunnerForTest,
  setGitHubPrStatusGitRunnerForTest,
} from "../src/actions/github-pr-status.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }
type GitResponse = { success: boolean; stdout: string; stderr: string; exitCode: number; combinedOutput: string }

const WORKSPACE_PATH = "/workspace"

afterEach(() => {
  setGitHubPrStatusGitRunnerForTest(null)
  setGitHubPrStatusGhRunnerForTest(null)
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
  }
}

function installGit(respond: (workDir: string, args: string[], signal: AbortSignal) => GitResponse | Promise<GitResponse>) {
  setGitHubPrStatusGitRunnerForTest(async (workDir, args, signal) => await respond(workDir, args, signal))
}

function installGh(respond: (command: string, args: string[], cwd: string) => CommandResult | Promise<CommandResult>) {
  setGitHubPrStatusGhRunnerForTest(async (cmd, args, cwd, _signal) => await respond(cmd, args, cwd))
}

const PR_VIEW_OPEN = JSON.stringify({
  number: 42,
  url: "https://github.com/acme/repo/pull/42",
  state: "OPEN",
  isDraft: false,
  baseRefName: "master",
  baseRefOid: "base-sha",
  headRefOid: "head-sha",
  headRefName: "mohist/run-wr-gh-status-1",
})

const PR_VIEW_DRAFT = JSON.stringify({
  number: 42,
  url: "https://github.com/acme/repo/pull/42",
  state: "OPEN",
  isDraft: true,
  baseRefName: "master",
  baseRefOid: "base-sha",
  headRefOid: "head-sha",
  headRefName: "mohist/run-wr-gh-status-1",
})

const PR_VIEW_MERGED = JSON.stringify({
  number: 42,
  url: "https://github.com/acme/repo/pull/42",
  state: "MERGED",
  isDraft: false,
  baseRefName: "master",
  baseRefOid: "base-sha",
  headRefOid: "head-sha",
  headRefName: "mohist/run-wr-gh-status-1",
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
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      ghCalls.push(full)
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.status).toBe("success")
    const parsed = JSON.parse(result.output!)
    expect(parsed.kind).toBe("github-pr-status")
    expect(parsed.status).toBe("verified")
    expect(parsed.prNumber).toBe(42)
    expect(parsed.prUrl).toBe("https://github.com/acme/repo/pull/42")
    expect(parsed.prState).toBe("OPEN")
    expect(parsed.isDraft).toBe(false)
    expect(parsed.expectations).toEqual(["open", "ready"])
    expect(parsed.missing).toEqual([])
  })

  it("rejects a draft PR by default", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_DRAFT)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.status).toBe("failure")
    const parsed = JSON.parse(result.output!)
    expect(parsed.expectations).toEqual(["open", "ready"])
    expect(parsed.missing).toEqual(["ready"])
  })

  it("rejects a non-open PR by default", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_MERGED)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.status).toBe("failure")
    const parsed = JSON.parse(result.output!)
    expect(parsed.expectations).toEqual(["open", "ready"])
    expect(parsed.missing).toEqual(["open", "ready"])
  })

  it("fails with expect=merged when the PR state is OPEN", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "merged" }))

    expect(result.status).toBe("failure")
    const parsed = JSON.parse(result.output!)
    expect(parsed.kind).toBe("github-pr-status")
    expect(parsed.status).toBe("failed")
    expect(parsed.missing).toContain("merged")
  })

  it("passes expect=merged when the PR state is MERGED", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_MERGED)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "merged" }))

    expect(result.status).toBe("success")
    const parsed = JSON.parse(result.output!)
    expect(parsed.status).toBe("verified")
    expect(parsed.missing).toEqual([])
  })

  it("rejects a draft PR when expect=ready is set", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_DRAFT)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "ready" }))

    expect(result.status).toBe("failure")
    const parsed = JSON.parse(result.output!)
    expect(parsed.missing).toContain("ready")
  })

  it("passes head-matches when local HEAD SHA equals PR head SHA", async () => {
    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      if (cmd === "rev-parse mohist/run-wr-gh-status-1") return ok("head-sha\n")
      return fail(`unexpected git call: ${cmd}`)
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "head-matches" }))

    expect(result.status).toBe("success")
    const parsed = JSON.parse(result.output!)
    expect(parsed.localHeadSha).toBe("head-sha")
    expect(parsed.headSha).toBe("head-sha")
    expect(parsed.missing).toEqual([])
  })

  it("fails head-matches when local HEAD SHA differs from PR head SHA", async () => {
    installGit((_workDir, args) => {
      const cmd = args.join(" ")
      if (cmd === "rev-parse mohist/run-wr-gh-status-1") return ok("different-sha\n")
      return fail(`unexpected git call: ${cmd}`)
    })
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "head-matches" }))

    expect(result.status).toBe("failure")
    const parsed = JSON.parse(result.output!)
    expect(parsed.missing).toContain("head-matches")
  })

  it("resolves prNumber from vars.github.pr.number when omitted from with", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 7")) return ghOk(PR_VIEW_OPEN.replace("42", "7"))
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({}, {
      github: { pr: { number: 7, url: "https://github.com/acme/repo/pull/7" } },
    }))

    expect(result.status).toBe("success")
    const parsed = JSON.parse(result.output!)
    expect(parsed.prNumber).toBe(7)
  })

  it("returns failure with a clear message when prNumber is missing", async () => {
    const result = await githubPrStatusAction(context({}))

    expect(result.status).toBe("failure")
    expect(result.message).toContain("prNumber")
  })

  it("returns failure when gh pr view fails", async () => {
    installGit(() => ok("head-sha\n"))
    installGh(() => ghFail("gh: not found"))

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.status).toBe("failure")
    expect(result.message).toContain("gh pr view 42 failed")
    const parsed = JSON.parse(result.output!)
    expect(parsed.kind).toBe("github-pr-status")
    expect(parsed.status).toBe("failed")
  })

  it("returns failure when gh pr view returns unparseable JSON", async () => {
    installGit(() => ok("head-sha\n"))
    installGh(() => ghOk("not-json"))

    const result = await githubPrStatusAction(context({ prNumber: 42 }))

    expect(result.status).toBe("failure")
    expect(result.message).toContain("unparseable JSON")
  })

  it("combines multiple expectations in the order they appear in expect", async () => {
    installGit(() => ok("head-sha\n"))
    installGh((cmd, args) => {
      const full = [cmd, ...args].join(" ")
      if (full.startsWith("gh pr view 42")) return ghOk(PR_VIEW_OPEN)
      return ghFail(`unexpected gh call: ${full}`)
    })

    const result = await githubPrStatusAction(context({ prNumber: 42, expect: "head-matches, in-sync, base-matches" }))

    expect(result.status).toBe("success")
    const parsed = JSON.parse(result.output!)
    expect(parsed.expectations).toEqual(["head-matches", "in-sync", "base-matches"])
    expect(parsed.missing).toEqual([])
  })

  it("ignores unknown expectation tokens", async () => {
    expect(parseGitHubPrStatusExpectation("merged, foo, in-sync")).toEqual(["merged", "in-sync"])
    expect(parseGitHubPrStatusExpectation(null)).toEqual(["open", "ready"])
    expect(parseGitHubPrStatusExpectation("")).toEqual(["open", "ready"])
  })
})
