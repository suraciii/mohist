import { describe, expect, it as vitestIt } from "vitest"
import { runCommand } from "../src/system/process.js"
import { git as defaultGit } from "../src/actions/git.js"
import { combinedGhOutput } from "../src/actions/github-pr-parse.js"
import {
  getGitHubPrGh,
  getGitHubPrGit,
  runGhPrecheck,
} from "../src/actions/github-pr-runtime.js"
import type { RunnerCommandRunner, RunnerFileSystem, RunnerGitRunner } from "../src/system/filesystem.js"
import { MemoryFileSystem } from "./support/memory-filesystem.js"
import { withTestRunnerResources } from "./support/test-resources.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }
type GitResult = CommandResult & { success: boolean; combinedOutput: string }
type GitRunner = RunnerGitRunner
type GhRunner = RunnerCommandRunner
type RuntimeTestResources = {
  fileSystem: RunnerFileSystem
  githubPrGitRunner?: RunnerGitRunner
  githubPrGhRunner?: RunnerCommandRunner
}

function it(name: string, body: (resources: RuntimeTestResources) => Promise<void> | void): void {
  vitestIt(name, async () => {
    const resources: RuntimeTestResources = { fileSystem: new MemoryFileSystem() }
    await withTestRunnerResources(async () => await body(resources), resources)
  })
}

function gitOk(): GitResult {
  return { exitCode: 0, stdout: "out", stderr: "", success: true, combinedOutput: "out" }
}

function ghOk(stdout: string, stderr = ""): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

function ghFail(stderr: string, stdout = "", exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

describe("github-pr-runtime: getGitHubPrGit", () => {
  it("returns the default git runner when no setter has been called", () => {
    expect(getGitHubPrGit()).toBe(defaultGit)
  })

  it("returns the scoped git runner", async (resources) => {
    const stub: GitRunner = async () => gitOk()
    resources.githubPrGitRunner = stub
    expect(getGitHubPrGit()).toBe(stub)
    expect(await getGitHubPrGit()("/virtual", ["rev-parse", "HEAD"], new AbortController().signal)).toEqual(gitOk())
  })

  it("uses the production git runner outside the scoped resource", async (resources) => {
    resources.githubPrGitRunner = async () => gitOk()
    expect(getGitHubPrGit()).not.toBe(defaultGit)
    await withTestRunnerResources(async () => {
      expect(getGitHubPrGit()).toBe(defaultGit)
    }, { fileSystem: resources.fileSystem })
  })
})

describe("github-pr-runtime: getGitHubPrGh", () => {
  it("returns the default gh runner when no resource is injected", () => {
    expect(getGitHubPrGh()).toBe(runCommand)
  })

  it("returns the scoped gh runner", async (resources) => {
    const stub: GhRunner = async () => ghOk("ok")
    resources.githubPrGhRunner = stub
    expect(getGitHubPrGh()).toBe(stub)
    expect(await getGitHubPrGh()("gh", ["--version"], "/virtual", new AbortController().signal)).toEqual(ghOk("ok"))
  })

  it("uses the production gh runner outside the scoped resource", async (resources) => {
    resources.githubPrGhRunner = async () => ghOk("ok")
    expect(getGitHubPrGh()).not.toBe(runCommand)
    await withTestRunnerResources(async () => {
      expect(getGitHubPrGh()).toBe(runCommand)
    }, { fileSystem: resources.fileSystem })
  })

  it("keeps one injected runner visible to every reader in the same resource", async (resources) => {
    const calls: Array<{ command: string; args: string[] }> = []
    const stub: GhRunner = async (command, args) => {
      calls.push({ command, args: [...args] })
      if (args[0] === "--version") return ghOk("gh version 2.0.0")
      return ghOk("logged in")
    }

    resources.githubPrGhRunner = stub
    const gh = getGitHubPrGh()
    expect(gh).toBe(stub)
    expect(getGitHubPrGh()).toBe(gh)

    const result = await stub("gh", ["--version"], "/virtual", new AbortController().signal)
    expect(calls).toEqual([{ command: "gh", args: ["--version"] }])
    expect(result.stdout).toBe("gh version 2.0.0")
  })
})

describe("github-pr-runtime: runGhPrecheck behavior (relocated from github-pr.ts)", () => {
  it("returns ok when both gh --version and gh auth status succeed", async () => {
    const stub: GhRunner = async (_cmd, args) => {
      if (args[0] === "--version") return ghOk("gh version 2.0.0 (2024-01-01)")
      return ghOk("Logged in to github.com as user")
    }
    const result = await runGhPrecheck(stub, "/virtual", new AbortController().signal)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.output).toContain("gh version 2.0.0")
      expect(result.output).toContain("Logged in to github.com as user")
    }
  })

  it("returns failure with a clear install message when gh --version exits non-zero", async () => {
    const stub: GhRunner = async () => ghFail("command not found: gh")
    const result = await runGhPrecheck(stub, "/virtual", new AbortController().signal)
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.exitCode).toBe(1)
      expect(result.output).toBe(combinedGhOutput({ stdout: "", stderr: "command not found: gh" }))
      expect(result.message).toMatch(/gh CLI is not installed or not on PATH/i)
      expect(result.message).toContain("gh auth login")
    }
  })

  it("returns failure with a clear auth message when gh --version succeeds but gh auth status fails", async () => {
    const stub: GhRunner = async (_cmd, args) => {
      if (args[0] === "--version") return ghOk("gh version 2.0.0 (2024-01-01)")
      return ghFail("You are not logged into any GitHub hosts.")
    }
    const result = await runGhPrecheck(stub, "/virtual", new AbortController().signal)
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.exitCode).toBe(1)
      expect(result.output).toContain("not logged into")
      expect(result.message).toMatch(/`gh auth status` did not return a logged-in account/i)
      expect(result.message).toContain("gh auth login")
    }
  })

  it("threads the provided gh runner parameter", async () => {
    const direct: GhRunner = async (_cmd, args) => {
      if (args[0] === "--version") return ghOk("gh version from direct param")
      return ghOk("ok")
    }
    const result = await runGhPrecheck(direct, "/virtual", new AbortController().signal)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.output).toContain("gh version from direct param")
    }
  })
})
