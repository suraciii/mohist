import { afterEach, describe, expect, it, vi } from "vitest"
import { killProcess, runCommand } from "../src/system/process.js"
import { createDefaultRegistry } from "../src/actions/registry.js"
import type { ChildProcess } from "node:child_process"
import type { ActionContext } from "../src/core/types.js"

// `runCommand` per-command timeout (issue-291, design D1–D5):
//   - optional `timeoutMs` rides in `CommandLineOptions`;
//   - omitted / non-positive ⇒ byte-identical result (no timer armed);
//   - on expiry the child + its process group are signaled (detached
//     spawn + negative-PID kill), captured output up to the kill is
//     preserved, and a sentinel line is appended to stderr;
//   - the resolved result carries `status: "timeout"` + `timeoutMs`;
//   - work-level abort still terminates the command exactly as before
//     (rejects); the per-command timer does not mask it.
//
// Tests use `vi.useFakeTimers({ toFake: ['setTimeout'] })` so the
// per-command timer (which is just `setTimeout`) is driven by the
// fake clock, while `setImmediate` / real I/O / child pipes remain
// on real time so captured output reaches the parent. Never real
// git/gh, never network, never wall-clock assertions.

const LINUX_DARWIN = process.platform !== "win32"

async function waitForProcessExit(pid: number) {
  for (let attempt = 0; attempt < 1_000; attempt += 1) {
    try {
      process.kill(pid, 0)
    } catch {
      return
    }
    await new Promise<void>((resolve) => setImmediate(resolve))
  }
  throw new Error(`Process ${pid} is still alive`)
}

async function waitForChildClose(child: ChildProcess) {
  await new Promise<void>((resolve) => child.once("close", () => resolve()))
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => { resolve = r })
  return { promise, resolve }
}

describe("runCommand per-command timeout", () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it("OmittedTimeout_PreservesByteIdenticalResult", async () => {
    // No `timeoutMs` ⇒ no timer armed, the resolved object is exactly
    // the pre-timeout shape: { exitCode, stdout, stderr }.
    const result = await runCommand(
      process.execPath,
      ["-e", "process.stdout.write('hello\\n'); process.exit(0)"],
      process.cwd(),
      new AbortController().signal,
    )

    expect(result).toEqual({ exitCode: 0, stdout: "hello\n", stderr: "" })
    expect("status" in result).toBe(false)
    expect("timeoutMs" in result).toBe(false)
  })

  it("NonPositiveTimeout_BehavesAsOmitted", async () => {
    const zero = await runCommand(
      process.execPath,
      ["-e", "process.exit(0)"],
      process.cwd(),
      new AbortController().signal,
      undefined,
      { timeoutMs: 0 },
    )
    const negative = await runCommand(
      process.execPath,
      ["-e", "process.exit(0)"],
      process.cwd(),
      new AbortController().signal,
      undefined,
      { timeoutMs: -50 },
    )

    for (const result of [zero, negative]) {
      expect(result).toEqual({ exitCode: 0, stdout: "", stderr: "" })
      expect("status" in result).toBe(false)
      expect("timeoutMs" in result).toBe(false)
    }
  })

  it("CommandExitsBeforeTimeout_HasNoTimeoutCategory", async () => {
    // Exits immediately — even with a long timeout, no `status`/`timeoutMs`
    // is serialized, and the captured output is the normal exit shape.
    vi.useFakeTimers({ toFake: ["setTimeout"] })
    const killSpy = vi.spyOn(process, "kill")
    try {
      const result = await runCommand(
        process.execPath,
        ["-e", "process.stdout.write('quick\\n'); process.exit(2)"],
        process.cwd(),
        new AbortController().signal,
        undefined,
        { timeoutMs: 10_000 },
      )

      expect(result.exitCode).toBe(2)
      expect(result.stdout).toBe("quick\n")
      expect(result.stderr).toBe("")
      expect("status" in result).toBe(false)
      expect("timeoutMs" in result).toBe(false)
      killSpy.mockClear()
      await vi.advanceTimersByTimeAsync(10_001)
      expect(killSpy).not.toHaveBeenCalled()
    } finally {
      killSpy.mockRestore()
      vi.useRealTimers()
    }
  })

  it("TimeoutExpires_KillsHungChildAndResolvesStructured", async () => {
    if (!LINUX_DARWIN) return // group kill semantics are POSIX-only

    const HANG_TIMEOUT_MS = 50

    vi.useFakeTimers({ toFake: ["setTimeout"] })
    try {
      const ready = deferred<void>()
      const promise = runCommand(
        process.execPath,
        ["-e", "process.stdout.write('partial-output\\n'); setInterval(() => {}, 1000)"],
        process.cwd(),
        new AbortController().signal,
        undefined,
        { timeoutMs: HANG_TIMEOUT_MS, onLine: (line) => { if (line === "partial-output") ready.resolve() } },
      )

      await ready.promise

      // Advance the fake setTimeout past the per-command timer. The
      // callback aborts the layered signal → killProcess + Node's
      // spawn-signal abort → the child is torn down.
      await vi.advanceTimersByTimeAsync(HANG_TIMEOUT_MS + 1)

      vi.useRealTimers()
      const result = await promise

      expect(result.status).toBe("timeout")
      expect(result.timeoutMs).toBe(HANG_TIMEOUT_MS)
      expect(result.exitCode).not.toBe(0)
      // Captured output up to the kill is preserved.
      expect(result.stdout).toContain("partial-output")
      // Sentinel appended so the unchanged `looksLikeRetrySafe` arm
      // (which already matches `timed out`) absorbs this as retry-safe.
      expect(result.stderr).toContain(`Command timed out after ${HANG_TIMEOUT_MS / 1000}s`)
    } finally {
      vi.useRealTimers()
    }
  })

  it("TimeoutExpires_ReapsHelperSubprocessWithParent", async () => {
    if (!LINUX_DARWIN) return

    // The direct child spawns a helper subprocess and writes both PIDs
    // to stdout, then waits. After the per-command timeout, both PIDs
    // must be reaped by the group kill (POSIX negative-PID kill).
    const helperScript = [
      "const { spawn } = require('child_process');",
      "const helper = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { detached: false, stdio: 'ignore' });",
      "process.stdout.write('parent=' + process.pid + ' helper=' + helper.pid + '\\n');",
      "setInterval(() => {}, 1000);",
    ].join("")

    const HANG_TIMEOUT_MS = 50

    vi.useFakeTimers({ toFake: ["setTimeout"] })
    const pids = deferred<{ parentPid: number; helperPid: number }>()
    let resolvedPids = false
    let parentPid: number | undefined
    let helperPid: number | undefined
    try {
      const promise = runCommand(
        process.execPath,
        ["-e", helperScript],
        process.cwd(),
        new AbortController().signal,
        undefined,
        {
          timeoutMs: HANG_TIMEOUT_MS,
          onLine: (line) => {
            const m = /^parent=(\d+) helper=(\d+)$/.exec(line)
            if (m) {
              parentPid = Number(m[1])
              helperPid = Number(m[2])
              if (!resolvedPids) {
                resolvedPids = true
                pids.resolve({ parentPid, helperPid })
              }
            }
          },
        },
      )

      ;({ parentPid, helperPid } = await pids.promise)

      await vi.advanceTimersByTimeAsync(HANG_TIMEOUT_MS + 1)

      vi.useRealTimers()
      const result = await promise

      expect(result.status).toBe("timeout")
      expect(result.timeoutMs).toBe(HANG_TIMEOUT_MS)
    } finally {
      vi.useRealTimers()
    }

    expect(parentPid).toBeDefined()
    expect(helperPid).toBeDefined()
    await waitForProcessExit(parentPid!)
    await waitForProcessExit(helperPid!)
  })

  it("ParentAbort_StillRejectsAndPerCommandTimerDoesNotMaskIt", async () => {
    if (!LINUX_DARWIN) return

    // Parent aborts before the per-command timer would fire. The
    // work-level abort path must dominate: the timer is cleared and
    // the promise rejects (today's behavior). No `status`/`timeoutMs`
    // are surfaced — this is a parent-abort, not a timeout.
    vi.useFakeTimers({ toFake: ["setTimeout"] })
    try {
      const controller = new AbortController()
      const ready = deferred<void>()
      const promise = runCommand(
        process.execPath,
        ["-e", "process.stdout.write('before-abort\\n'); setInterval(() => {}, 1000)"],
        process.cwd(),
        controller.signal,
        undefined,
        { timeoutMs: 60_000, onLine: (line) => { if (line === "before-abort") ready.resolve() } },
      )

      await ready.promise
      controller.abort(new Error("work-level abort"))

      vi.useRealTimers()
      await expect(promise).rejects.toBeTruthy()
    } finally {
      vi.useRealTimers()
    }
  })

  it("CoreScriptTimeout_RemainsParentAbortAndRejects", async () => {
    if (!LINUX_DARWIN) return

    const registry = createDefaultRegistry()
    const action = registry.resolve("core/script")
    if (!action) throw new Error("core/script action is not registered")

    vi.useFakeTimers({ toFake: ["setTimeout"] })
    try {
      const ready = deferred<void>()
      const promise = action({
        workflowRunId: "wr-timeout",
        workId: "script-timeout",
        workType: "task",
        stage: "check",
        title: "script timeout",
        uses: "core/script",
        with: { run: "printf 'script-ready\\n'; while true; do sleep 1; done", timeout: 50 },
        variables: {},
        workDir: process.cwd(),
        signal: new AbortController().signal,
        writeVars: async () => {},
        log: { write: (_source: string, text: string) => { if (text === "script-ready") ready.resolve(); return 1 } } as never,
      } satisfies ActionContext)

      await ready.promise
      await vi.advanceTimersByTimeAsync(51)

      vi.useRealTimers()
      await expect(promise).rejects.toBeTruthy()
    } finally {
      vi.useRealTimers()
    }
  })

  it("KillProcess_GroupKillsDetachedChild", async () => {
    if (!LINUX_DARWIN) return

    // Spawn a detached child that itself spawns a helper, then call
    // `killProcess` directly to verify the group-kill semantics. No
    // fake timers needed here — `killProcess` is synchronous.
    vi.useFakeTimers({ toFake: ["setTimeout"] })
    try {
      const { spawn } = await import("node:child_process")
      const parentScript = [
        "const { spawn } = require('child_process');",
        "const helper = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { detached: false, stdio: 'ignore' });",
        "process.stdout.write('parent=' + process.pid + ' helper=' + helper.pid + '\\n');",
        "setInterval(() => {}, 1000);",
      ].join("")

      const child = spawn(process.execPath, ["-e", parentScript], { cwd: process.cwd(), detached: true, stdio: ["ignore", "pipe", "ignore"] })
      const pids = deferred<{ parentPid: number; helperPid: number }>()
      let resolvedPids = false
      let parentPid: number | undefined
      let helperPid: number | undefined
      child.stdout!.on("data", (chunk: Buffer) => {
        const text = chunk.toString("utf8")
        const m = /^parent=(\d+) helper=(\d+)$/m.exec(text)
        if (m) {
          parentPid = Number(m[1])
          helperPid = Number(m[2])
          if (!resolvedPids) {
            resolvedPids = true
            pids.resolve({ parentPid, helperPid })
          }
        }
      })

      ;({ parentPid, helperPid } = await pids.promise)

      expect(parentPid).toBeDefined()
      expect(helperPid).toBeDefined()
      // Both alive before the kill.
      expect(() => process.kill(parentPid!, 0)).not.toThrow()
      expect(() => process.kill(helperPid!, 0)).not.toThrow()

      killProcess(child)

      await waitForProcessExit(parentPid!)
      await waitForProcessExit(helperPid!)
    } finally {
      vi.useRealTimers()
    }
  })

  it("KillProcess_FallsBackToDirectKillForNonDetachedChild", async () => {
    if (!LINUX_DARWIN) return

    const { spawn } = await import("node:child_process")
    const child = spawn(process.execPath, ["-e", "process.stdout.write('pid=' + process.pid + '\\n'); setInterval(() => {}, 1000)"], { cwd: process.cwd(), detached: false, stdio: ["ignore", "pipe", "ignore"] })
    const ready = deferred<number>()
    let resolvedPid = false
    let childPid: number | undefined
    child.stdout!.on("data", (chunk: Buffer) => {
      const m = /^pid=(\d+)$/m.exec(chunk.toString("utf8"))
      if (m) {
        childPid = Number(m[1])
        if (!resolvedPid) {
          resolvedPid = true
          ready.resolve(childPid)
        }
      }
    })

    try {
      childPid = await ready.promise
      expect(() => process.kill(childPid!, 0)).not.toThrow()

      killProcess(child)
      await waitForChildClose(child)

      expect(() => process.kill(childPid!, 0)).toThrow()
    } finally {
      killProcess(child)
    }
  })
})
