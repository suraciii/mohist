import { afterEach, beforeEach, describe, expect, it } from "vitest"
import {
  runCommand,
  setProcessKillerForTest,
  setProcessSpawnerForTest,
} from "../src/system/process.js"
import { FakeProcessSpawner } from "./support/fake-process.js"

describe("runCommand output", () => {
  let spawner: FakeProcessSpawner

  beforeEach(() => {
    spawner = new FakeProcessSpawner()
    setProcessSpawnerForTest(spawner.spawn)
  })

  afterEach(() => {
    setProcessSpawnerForTest(null)
    setProcessKillerForTest(null)
  })

  it("StreamsLinesAndPreservesAggregateOutput", async () => {
    const lines: string[] = []
    const result = runCommand("command", ["--flag"], "/workspace", new AbortController().signal, undefined, {
      onLine: (line) => lines.push(line),
    })
    const child = spawner.children[0]!

    child.writeStdout("out-1\nout-2")
    child.writeStderr("err-1\n")
    child.close(0)

    await expect(result).resolves.toEqual({
      exitCode: 0,
      stdout: "out-1\nout-2",
      stderr: "err-1\n",
    })
    expect(lines).toEqual(["out-1", "err-1", "out-2"])
    expect(spawner.calls).toHaveLength(1)
  })

  it("DecodesSplitUtf8AndFlushesTrailingLinesBeforeClose", async () => {
    const lines: string[] = []
    const closes: number[] = []
    const result = runCommand("command", [], "/workspace", new AbortController().signal, undefined, {
      onLine: (line) => lines.push(line),
      onClose: (code) => closes.push(code),
    })
    const child = spawner.children[0]!
    const bytes = Buffer.from("file-文件\n")

    child.writeStdout(bytes.subarray(0, 7))
    child.writeStdout(bytes.subarray(7))
    child.writeStderr("tail")
    child.close(7)

    await expect(result).resolves.toMatchObject({ exitCode: 7, stdout: "file-文件\n", stderr: "tail" })
    expect(lines).toEqual(["file-文件", "tail"])
    expect(closes).toEqual([7])
  })
})
