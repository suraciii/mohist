import { EventEmitter } from "node:events"
import { PassThrough } from "node:stream"
import { afterEach, describe, expect, it, vi } from "vitest"
import { runCommand, setProcessKillerForTest, setProcessSpawnerForTest, type ProcessSpawner } from "./process.js"

function fakeChild(pid: number): ReturnType<ProcessSpawner> & { stdout: PassThrough; stderr: PassThrough } {
  const stdout = new PassThrough()
  const stderr = new PassThrough()
  const child = new EventEmitter() as ReturnType<ProcessSpawner>
  return Object.assign(child, { pid, stdout, stderr })
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
    const signals: Array<[number, string | number | undefined]> = []
    setProcessSpawnerForTest(() => child)
    setProcessKillerForTest(((pid, signal) => {
      signals.push([pid, signal!])
      return true
    }) as typeof process.kill)

    const result = runCommand("bash", ["script.sh"], ".", new AbortController().signal, undefined, { timeoutMs: 1 })
    await vi.advanceTimersByTimeAsync(1)
    child.emit("exit", null)
    child.emit("close", null)

    await expect(result).resolves.toMatchObject({ status: "timeout", timeoutMs: 1 })
    await vi.advanceTimersByTimeAsync(5_000)

    expect(signals).toEqual([[-4242, "SIGTERM"], [-4242, "SIGKILL"]])
  })

  it("drains output before settling and terminates descendants that retain inherited pipes", async () => {
    const child = fakeChild(4343)
    const signals: Array<[number, string | number | undefined]> = []
    setProcessSpawnerForTest(() => child)
    setProcessKillerForTest(((pid, signal) => {
      signals.push([pid, signal!])
      return true
    }) as typeof process.kill)

    const result = runCommand("bash", ["script.sh"], ".", new AbortController().signal)
    let settled = false
    void result.then(() => { settled = true })

    child.stdout.write("before-exit\n")
    child.emit("exit", 0)
    child.stdout.write("after-exit\n")
    child.stderr.write("diagnostic\n")
    await Promise.resolve()

    expect(settled).toBe(false)
    expect(signals).toEqual([[-4343, "SIGKILL"]])

    child.emit("close", 0)

    await expect(result).resolves.toEqual({
      exitCode: 0,
      stdout: "before-exit\nafter-exit\n",
      stderr: "diagnostic\n",
    })
  })
})
