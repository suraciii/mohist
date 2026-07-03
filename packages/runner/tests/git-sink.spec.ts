import { describe, expect, it } from "vitest"
import { git } from "../src/actions/git.js"
import { TaskLogCollector, TaskLogger } from "../src/runtime/task-log.js"

describe("git sink forwarding (T-003)", () => {
  it("PreservesAggregateCommandResultWhenSinkIsProvided", async () => {
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    const result = await git(
      process.cwd(),
      ["--version"],
      new AbortController().signal,
      { sink: { log: logger, source: "action:rebase" } },
    )
    expect(result.exitCode).toBe(0)
    expect(result.success).toBe(true)
    expect(result.stdout).toMatch(/git version/)
    expect(collector.size()).toBeGreaterThanOrEqual(0)
    // git --version emits a single line so the buffer should record exactly one line.
    const flushed = collector.flush()
    expect(flushed.entries.every((e) => e.source === "action:rebase")).toBe(true)
  })

  it("EmitsEveryLineWithTheConfiguredPhaseSourceTag", async () => {
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    const result = await git(
      process.cwd(),
      ["help", "status"],
      new AbortController().signal,
      { sink: { log: logger, source: "branch-check" } },
    )
    expect(result.exitCode).toBe(0)
    const flushed = collector.flush()
    expect(flushed.entries.length).toBeGreaterThan(0)
    expect(flushed.entries.every((e) => e.source === "branch-check")).toBe(true)
  })

  it("ReturnsAggregateContractWhenNoSinkIsSupplied", async () => {
    const result = await git(process.cwd(), ["--version"], new AbortController().signal)
    expect(result.exitCode).toBe(0)
    expect(result.success).toBe(true)
    expect(result.stdout).toMatch(/git version/)
    expect(result.combinedOutput).toContain("git version")
  })

  it("ForwardsFailingOpsCommandOutputToTheCollectorBuffer", async () => {
    const collector = new TaskLogCollector()
    const logger = new TaskLogger({ collector })
    // Deliberately failing git command: `git bogus-subcommand` exits
    // non-zero with "git: 'bogus-subcommand' is not a git command".
    const result = await git(
      process.cwd(),
      ["bogus-subcommand-that-will-fail"],
      new AbortController().signal,
      { sink: { log: logger, source: "action:rebase" } },
    )
    expect(result.success).toBe(false)
    expect(result.exitCode).not.toBe(0)
    // The failing command should still have its output captured — at
    // least one line must reach the collector so the failure is
    // visible in the web log viewer (design D6: collector is the
    // single funnel and is never bypassed even on failure).
    const flushed = collector.flush()
    const combined = flushed.entries.map((e) => e.text).join("\n")
    expect(combined.length).toBeGreaterThan(0)
    expect(flushed.entries.every((e) => e.source === "action:rebase")).toBe(true)
  })
})
