import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import {
  killProcess,
  runCommand,
  setProcessKillerForTest,
  setProcessSpawnerForTest,
} from "../src/system/process.js"
import { FakeChildProcess, FakeProcessSpawner } from "./support/fake-process.js"

describe("runCommand timeout", () => {
  let spawner: FakeProcessSpawner
  let kills: Array<{ pid: number, signal: string | number | undefined }>

  beforeEach(() => {
    spawner = new FakeProcessSpawner()
    kills = []
    setProcessSpawnerForTest(spawner.spawn)
    setProcessKillerForTest((pid, signal) => {
      kills.push({ pid, signal })
      return true
    })
  })

  afterEach(() => {
    vi.useRealTimers()
    setProcessSpawnerForTest(null)
    setProcessKillerForTest(null)
  })

  it("OmitsTimeoutFieldsForDisabledTimeouts", async () => {
    const zero = runCommand("command", [], "/workspace", new AbortController().signal, undefined, { timeoutMs: 0 })
    spawner.children[0]!.close(0)
    const negative = runCommand("command", [], "/workspace", new AbortController().signal, undefined, { timeoutMs: -1 })
    spawner.children[1]!.close(0)

    await expect(zero).resolves.toEqual({ exitCode: 0, stdout: "", stderr: "" })
    await expect(negative).resolves.toEqual({ exitCode: 0, stdout: "", stderr: "" })
  })

  it("CompletesBeforeItsTimeoutWithoutKilling", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout"] })
    const result = runCommand("command", [], "/workspace", new AbortController().signal, undefined, { timeoutMs: 100 })
    const child = spawner.children[0]!

    child.writeStdout("quick\n")
    child.close(2)
    await expect(result).resolves.toEqual({ exitCode: 2, stdout: "quick\n", stderr: "" })
    await vi.advanceTimersByTimeAsync(101)
    expect(kills).toEqual([])
  })

  it("TimeoutKillsTheProcessGroupAndReturnsStructuredResult", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout"] })
    const lines: string[] = []
    const result = runCommand("command", [], "/workspace", new AbortController().signal, undefined, {
      timeoutMs: 50,
      onLine: (line) => lines.push(line),
    })
    const child = spawner.children[0]!

    child.writeStdout("partial-output\n")
    await vi.advanceTimersByTimeAsync(51)

    if (process.platform === "win32") {
      expect(child.killSignals).toEqual(["SIGTERM"])
    } else {
      expect(kills).toEqual([{ pid: -child.pid, signal: "SIGTERM" }])
    }
    child.close(143)

    await expect(result).resolves.toEqual({
      exitCode: 143,
      stdout: "partial-output\n",
      stderr: "Command timed out after 0.05s\n",
      status: "timeout",
      timeoutMs: 50,
    })
    expect(lines).toEqual(["partial-output"])
  })

  it("ParentAbortRejectsInsteadOfProducingTimeout", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout"] })
    const controller = new AbortController()
    const result = runCommand("command", [], "/workspace", controller.signal)
    const child = spawner.children[0]!

    controller.abort(new Error("cancelled"))
    child.fail(new Error("cancelled"))

    await expect(result).rejects.toThrow("cancelled")
    await vi.advanceTimersByTimeAsync(5_000)
    if (process.platform === "win32") {
      expect(child.killSignals).toEqual(["SIGTERM", "SIGKILL"])
    } else {
      expect(kills).toEqual([
        { pid: -child.pid, signal: "SIGTERM" },
        { pid: -child.pid, signal: "SIGKILL" },
      ])
    }
  })

  it("FallsBackToDirectChildKillWhenGroupKillFails", () => {
    const child = new FakeChildProcess()
    setProcessKillerForTest(() => {
      throw new Error("no process group")
    })

    killProcess(child as never, "SIGKILL")

    expect(child.killSignals).toEqual(["SIGKILL"])
  })
})
