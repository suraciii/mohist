import { afterEach, describe, expect, it } from "vitest"
import { runCommand } from "../src/system/process.js"
import { git as defaultGit } from "../src/actions/git.js"
import {
  setGitHubPrGhRunnerForTest,
  setGitHubPrGitRunnerForTest,
} from "../src/actions/github-pr.js"
import { combinedGhOutput } from "../src/actions/github-pr-parse.js"
import {
  getGitHubPrGh,
  getGitHubPrGit,
  runGhPrecheck,
} from "../src/actions/github-pr-runtime.js"

type CommandResult = { exitCode: number; stdout: string; stderr: string }
type GitResult = CommandResult & { success: boolean; combinedOutput: string }
type GitRunner = typeof defaultGit
type GhRunner = typeof runCommand

afterEach(() => {
  setGitHubPrGitRunnerForTest(null)
  setGitHubPrGhRunnerForTest(null)
})

function gitOk(): GitResult {
  return { exitCode: 0, stdout: "out", stderr: "", success: true, combinedOutput: "out" }
}

function ghOk(stdout: string, stderr = ""): CommandResult {
  return { exitCode: 0, stdout, stderr }
}

function ghFail(stderr: string, stdout = "", exitCode = 1): CommandResult {
  return { exitCode, stdout, stderr }
}

describe("github-pr-runtime: getGitHubPrGit / setGitHubPrGitRunnerForTest", () => {
  it("returns the default git runner when no setter has been called", () => {
    expect(getGitHubPrGit()).toBe(defaultGit)
  })

  it("returns a stub git runner after setGitHubPrGitRunnerForTest", async () => {
    const stub: GitRunner = async () => gitOk()
    setGitHubPrGitRunnerForTest(stub)
    expect(getGitHubPrGit()).toBe(stub)
    expect(await getGitHubPrGit()("/tmp", ["rev-parse", "HEAD"], new AbortController().signal)).toEqual(gitOk())
  })

  it("setGitHubPrGitRunnerForTest(null) resets to the default git runner", () => {
    setGitHubPrGitRunnerForTest(async () => gitOk())
    expect(getGitHubPrGit()).not.toBe(defaultGit)
    setGitHubPrGitRunnerForTest(null)
    expect(getGitHubPrGit()).toBe(defaultGit)
  })
})

describe("github-pr-runtime: getGitHubPrGh / setGitHubPrGhRunnerForTest", () => {
  it("returns the default gh runner when no setter has been called", () => {
    expect(getGitHubPrGh()).toBe(runCommand)
  })

  it("returns a stub gh runner after setGitHubPrGhRunnerForTest", async () => {
    const stub: GhRunner = async () => ghOk("ok")
    setGitHubPrGhRunnerForTest(stub)
    expect(getGitHubPrGh()).toBe(stub)
    expect(await getGitHubPrGh()("gh", ["--version"], "/tmp", new AbortController().signal)).toEqual(ghOk("ok"))
  })

  it("setGitHubPrGhRunnerForTest(null) resets to the default gh runner", () => {
    setGitHubPrGhRunnerForTest(async () => ghOk("ok"))
    expect(getGitHubPrGh()).not.toBe(runCommand)
    setGitHubPrGhRunnerForTest(null)
    expect(getGitHubPrGh()).toBe(runCommand)
  })

  it("one setGitHubPrGhRunnerForTest call updates the single runner read by the next getter — no per-module duplicate setters", async () => {
    const calls: Array<{ command: string; args: string[] }> = []
    const stub: GhRunner = async (command, args, _workDir, _signal) => {
      calls.push({ command, args: [...args] })
      if (args[0] === "--version") return ghOk("gh version 2.0.0")
      return ghOk("logged in")
    }

    setGitHubPrGhRunnerForTest(stub)
    const gh = getGitHubPrGh()
    expect(gh).toBe(stub)

    // The same singleton is read by both the create-side precheck and any other consumer.
    const fromAnotherReader = getGitHubPrGh()
    expect(fromAnotherReader).toBe(stub)
    expect(fromAnotherReader).toBe(gh)

    const result = await stub("gh", ["--version"], "/tmp", new AbortController().signal)
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
    const result = await runGhPrecheck(stub, "/tmp", new AbortController().signal)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.output).toContain("gh version 2.0.0")
      expect(result.output).toContain("Logged in to github.com as user")
    }
  })

  it("returns failure with a clear install message when gh --version exits non-zero", async () => {
    const stub: GhRunner = async () => ghFail("command not found: gh")
    const result = await runGhPrecheck(stub, "/tmp", new AbortController().signal)
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
    const result = await runGhPrecheck(stub, "/tmp", new AbortController().signal)
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.exitCode).toBe(1)
      expect(result.output).toContain("not logged into")
      expect(result.message).toMatch(/`gh auth status` did not return a logged-in account/i)
      expect(result.message).toContain("gh auth login")
    }
  })

  it("threads the provided gh runner param rather than reading module-scope state", async () => {
    // Two distinct runners; the call must respect the param even when the
    // module-scope singleton points elsewhere.
    setGitHubPrGhRunnerForTest(async () => ghFail("module-scope runner must not be called"))
    const direct: GhRunner = async (_cmd, args) => {
      if (args[0] === "--version") return ghOk("gh version from direct param")
      return ghOk("ok")
    }
    const result = await runGhPrecheck(direct, "/tmp", new AbortController().signal)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.output).toContain("gh version from direct param")
    }
  })
})

describe("github-pr-runtime: barrel re-export from github-pr.js", () => {
  it("exposes the two runner setters via the barrel so the three specs keep injecting", () => {
    expect(typeof setGitHubPrGitRunnerForTest).toBe("function")
    expect(typeof setGitHubPrGhRunnerForTest).toBe("function")
  })
})
