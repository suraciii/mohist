import { describe, expect, it } from "vitest"
import { runCommand } from "../../src/system/process.js"

function spawnScriptWithLineCallback(script: string, onLine: (line: string) => void) {
  return runCommand(process.execPath, ["-e", script], process.cwd(), new AbortController().signal, undefined, {
    onLine,
  })
}

function spawnScript(script: string) {
  return runCommand(process.execPath, ["-e", script], process.cwd(), new AbortController().signal)
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => { resolve = r })
  return { promise, resolve }
}

describe("runCommand onLine callback", () => {
  it("EmitsLinesFromStdoutPreservingAggregate", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "process.stdout.write('hello\\nworld\\n')",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["hello", "world"])
    expect(result.stdout).toBe("hello\nworld\n")
    expect(result.exitCode).toBe(0)
  })

  it("EmitsTrailingLineWithoutNewlineAsFinalLine", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "process.stdout.write('alpha\\nbeta')",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["alpha", "beta"])
    expect(result.stdout).toBe("alpha\nbeta")
  })

  it("MergesLinesFromStdoutAndStderrWithoutAssumingCrossStreamOrder", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "process.stdout.write('out-1\\n'); process.stderr.write('err-1\\n'); process.stdout.write('out-2')",
      (line) => lines.push(line),
    )

    expect([...lines].sort()).toEqual(["err-1", "out-1", "out-2"])
    expect(result.stdout).toBe("out-1\nout-2")
    expect(result.stderr).toBe("err-1\n")
  })

  it("DrainsBufferedTailAfterClose", async () => {
    const lines: string[] = []
    await spawnScriptWithLineCallback(
      "process.stderr.write('partial-tail'); process.exitCode = 2",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["partial-tail"])
  })

  it("EmitsOnCloseOnceAfterDrain", async () => {
    const lines: string[] = []
    const exits: number[] = []
    await runCommand(
      process.execPath,
      ["-e", "process.stdout.write('ok\\n'); process.exitCode = 7"],
      process.cwd(),
      new AbortController().signal,
      undefined,
      {
        onLine: (line) => lines.push(line),
        onClose: (code) => exits.push(code),
      },
    )

    expect(lines).toEqual(["ok"])
    expect(exits).toEqual([7])
  })

  it("WithoutOnLine_PreservesExistingCommandResultContract", async () => {
    const result = await spawnScript(
      "process.stdout.write('alpha\\nbeta'); process.stderr.write('gamma\\n'); process.exitCode = 3",
    )

    expect(result.stdout).toBe("alpha\nbeta")
    expect(result.stderr).toBe("gamma\n")
    expect(result.exitCode).toBe(3)
  })

  it("ByteIdenticalAggregateWithAndWithoutOnLine", async () => {
    const script = "process.stdout.write('one\\ntwo\\n'); process.stderr.write('err\\n')"
    const without = await runCommand(process.execPath, ["-e", script], process.cwd(), new AbortController().signal)
    const withLines = await spawnScriptWithLineCallback(script, () => undefined)

    expect(withLines).toEqual(without)
  })

  it("HandlesCrlfBoundariesAndPreservesLineContent", async () => {
    const lines: string[] = []
    await spawnScriptWithLineCallback(
      "process.stdout.write('crlf\\r\\nlf\\n')",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["crlf", "lf"])
  })

  it("DecodesUtf8SplitAcrossChunksWithoutReplacementCharacters", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "const bytes = Buffer.from('文件\\n'); process.stdout.write(bytes.subarray(0, 1)); setImmediate(() => { process.stdout.write(bytes.subarray(1)) })",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["文件"])
    expect(result.stdout).toBe("文件\n")
  })
})

describe("runCommand signal handling", () => {
  it("AbortsAfterChildSignalsReadiness", async () => {
    const controller = new AbortController()
    const ready = deferred<void>()
    const promise = runCommand(
      process.execPath,
      ["-e", "process.stdout.write('ready\\n'); setInterval(() => {}, 1000)"],
      process.cwd(),
      controller.signal,
      undefined,
      { onLine: (line) => { if (line === "ready") ready.resolve() } },
    )

    await ready.promise
    controller.abort()
    await expect(promise).rejects.toBeTruthy()
  })
})
