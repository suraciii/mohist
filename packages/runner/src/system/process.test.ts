import { EventEmitter } from "node:events"
import { PassThrough } from "node:stream"
import { describe, expect, it as vitestIt, vi } from "vitest"
import { runCommand, type ProcessSpawner } from "./process.js"
import { withTestRunnerResources } from "../../tests/support/test-resources.js"

function fakeChild(pid: number): ReturnType<ProcessSpawner> & { stdout: PassThrough; stderr: PassThrough } {
  const stdout = new PassThrough()
  const stderr = new PassThrough()
  const child = new EventEmitter() as ReturnType<ProcessSpawner>
  return Object.assign(child, { pid, stdout, stderr })
}

describe("runCommand cancellation", () => {
  vitestIt("returns a timeout result and force-kills descendants after the shell exits", async () => {
    vi.useFakeTimers()
    const child = fakeChild(4242)
    const signals: Array<[number, string | number | undefined]> = []
    return withTestRunnerResources(
      async () => {
        try {
          const result = runCommand("bash", ["script.sh"], ".", new AbortController().signal, undefined, { timeoutMs: 1 })
          await vi.advanceTimersByTimeAsync(1)
          child.emit("exit", null)

          await expect(result).resolves.toMatchObject({ status: "timeout", timeoutMs: 1 })
          await vi.advanceTimersByTimeAsync(5_000)

          expect(signals).toEqual([[-4242, "SIGTERM"], [-4242, "SIGKILL"]])
        } finally {
          vi.useRealTimers()
        }
      },
      {
        processSpawner: () => child,
        processKiller: (pid, signal) => { signals.push([pid, signal]); return true },
      },
    )
  })

  vitestIt("settles when the direct child exits even if inherited pipes stay open", async () => {
    const child = fakeChild(4343)
    return withTestRunnerResources(
      async () => {
        try {
          const result = runCommand("bash", ["script.sh"], ".", new AbortController().signal)
          child.stdout.write("done\n")
          child.emit("exit", 0)
          await expect(result).resolves.toEqual({ exitCode: 0, stdout: "done\n", stderr: "" })
        } finally {
          vi.useRealTimers()
        }
      },
      { processSpawner: () => child },
    )
  })
})
