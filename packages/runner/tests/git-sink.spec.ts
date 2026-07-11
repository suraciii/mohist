import { afterEach, describe, expect, it, vi } from "vitest"
import { git } from "../src/actions/git.js"
import { TaskLogCollector, TaskLogger } from "../src/runtime/task-log.js"
import * as processModule from "../src/system/process.js"
import type { CommandLineOptions, CommandResult } from "../src/system/process.js"

type GitCall = {
  command: string
  args: string[]
  workDir: string
  options: CommandLineOptions | undefined
}

let restoreGitRunner: (() => void) | null = null

afterEach(() => {
  restoreGitRunner?.()
  restoreGitRunner = null
})

function installGitRunner(respond: (call: GitCall) => CommandResult) {
  const calls: GitCall[] = []
  const runner = vi.spyOn(processModule, "runCommand").mockImplementation(async (command, args, workDir, _signal, _env, options) => {
    const call = { command, args: [...args], workDir, options }
    calls.push(call)
    const result = respond(call)
    for (const line of outputLines(result.stdout)) options?.onLine?.(line)
    for (const line of outputLines(result.stderr)) options?.onLine?.(line)
    options?.onClose?.(result.exitCode)
    return result
  })
  restoreGitRunner = () => runner.mockRestore()
  return calls
}

function outputLines(output: string) {
  return output.split(/\r?\n/).filter(Boolean)
}

describe("git forwards command output to the task log", () => {
  it("PreservesAggregateCommandResultWhenSinkIsProvided", async () => {
    const calls = installGitRunner(() => ({
      exitCode: 0,
      stdout: "git version 2.45.0\n",
      stderr: "",
    }))
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    const result = await git(
      "/workspace",
      ["--version"],
      new AbortController().signal,
      { sink: { log: logger, source: "action:rebase" } },
    )
    expect(result.exitCode).toBe(0)
    expect(result.success).toBe(true)
    expect(result.stdout).toMatch(/git version/)
    expect(collector.size()).toBeGreaterThanOrEqual(0)
    const flushed = collector.flush()
    expect(flushed.entries.every((e) => e.source === "action:rebase")).toBe(true)
    expect(calls).toEqual([
      expect.objectContaining({ command: "git", args: ["--version"], workDir: "/workspace" }),
    ])
  })

  it("EmitsEveryLineWithTheConfiguredPhaseSourceTag", async () => {
    installGitRunner(() => ({
      exitCode: 0,
      stdout: "usage: git status\nstatus options\n",
      stderr: "",
    }))
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    const result = await git(
      "/workspace",
      ["help", "status"],
      new AbortController().signal,
      { sink: { log: logger, source: "branch-check" } },
    )
    expect(result.exitCode).toBe(0)
    const flushed = collector.flush()
    expect(flushed.entries.length).toBeGreaterThan(0)
    expect(flushed.entries.every((e) => e.source === "branch-check")).toBe(true)
    expect(flushed.entries.map((entry) => entry.text)).toEqual(["usage: git status", "status options"])
  })

  it("ReturnsAggregateContractWhenNoSinkIsSupplied", async () => {
    const calls = installGitRunner(() => ({
      exitCode: 0,
      stdout: "git version 2.45.0\n",
      stderr: "",
    }))
    const result = await git("/workspace", ["--version"], new AbortController().signal)
    expect(result.exitCode).toBe(0)
    expect(result.success).toBe(true)
    expect(result.stdout).toMatch(/git version/)
    expect(result.combinedOutput).toContain("git version")
    expect(calls[0]?.options?.onLine).toBeUndefined()
  })

  it("ForwardsFailingOpsCommandOutputToTheCollectorBuffer", async () => {
    installGitRunner(() => ({
      exitCode: 129,
      stdout: "",
      stderr: "git: 'bogus-subcommand-that-will-fail' is not a git command\n",
    }))
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    const result = await git(
      "/workspace",
      ["bogus-subcommand-that-will-fail"],
      new AbortController().signal,
      { sink: { log: logger, source: "action:rebase" } },
    )
    expect(result.success).toBe(false)
    expect(result.exitCode).not.toBe(0)
    const flushed = collector.flush()
    const combined = flushed.entries.map((e) => e.text).join("\n")
    expect(combined.length).toBeGreaterThan(0)
    expect(flushed.entries.every((e) => e.source === "action:rebase")).toBe(true)
  })
})
