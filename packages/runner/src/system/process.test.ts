import { EventEmitter } from "node:events"
import { PassThrough } from "node:stream"
import type { ChildProcessWithoutNullStreams } from "node:child_process"
import { afterEach, describe, expect, it, vi } from "vitest"
import { runCommand, setProcessKillerForTest, setProcessSpawnerForTest } from "./process.js"

function fakeChild(pid: number) {
  const child = new EventEmitter() as ChildProcessWithoutNullStreams
  Object.assign(child, { pid, stdout: new PassThrough(), stderr: new PassThrough() })
  return child
}

afterEach(() => {
  setProcessSpawnerForTest(null)
  setProcessKillerForTest(null)
  vi.useRealTimers()
})

describe("runCommand cancellation", () => {
  it("returns a timeout result and force-kills descendants after the shell exits", async () => {
    vi.useFakeTimers()
    const child = fakeChild(4242)
    const signals: Array<[number, NodeJS.Signals]> = []
    setProcessSpawnerForTest(() => child)
    setProcessKillerForTest(((pid, signal) => {
      signals.push([pid, signal!])
      return true
    }) as typeof process.kill)

    const result = runCommand("bash", ["script.sh"], ".", new AbortController().signal, undefined, { timeoutMs: 1 })
    await vi.advanceTimersByTimeAsync(1)
    child.emit("exit", null)

    await expect(result).resolves.toMatchObject({ status: "timeout", timeoutMs: 1 })
    await vi.advanceTimersByTimeAsync(5_000)

    expect(signals).toEqual([[-4242, "SIGTERM"], [-4242, "SIGKILL"]])
  })

  it("settles when the direct child exits even if inherited pipes stay open", async () => {
    const child = fakeChild(4343)
    setProcessSpawnerForTest(() => child)

    const result = runCommand("bash", ["script.sh"], ".", new AbortController().signal)
    child.stdout.write("done\n")
    child.emit("exit", 0)

    await expect(result).resolves.toEqual({ exitCode: 0, stdout: "done\n", stderr: "" })
  })
})
