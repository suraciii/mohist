import { describe, expect, it } from "vitest"
import { runCommand, sanitizedEnvironment } from "../src/system/process.js"

function spawnScriptWithLineCallback(script: string, onLine: (line: string) => void) {
  return runCommand(process.execPath, ["-e", script], process.cwd(), new AbortController().signal, undefined, {
    onLine,
  })
}

function spawnScript(script: string) {
  return runCommand(process.execPath, ["-e", script], process.cwd(), new AbortController().signal)
}

describe("sanitizedEnvironment", () => {
  it("RunnerSpawnedAgent_DisablesToolSelfUpdateNoise", () => {
    const env = sanitizedEnvironment({})

    expect(env.OPENCODE_DISABLE_UPDATE_CHECK).toBe("1")
    expect(env.OPENCODE_DISABLE_AUTO_UPDATE).toBe("1")
    expect(env.NO_UPDATE_NOTIFIER).toBe("1")
  })

  it("RunnerSpawnedAgent_DoesNotForwardOpencodeServerCredentials", () => {
    const env = sanitizedEnvironment({
      OPENCODE_SERVER_PASSWORD: "secret",
      OPENCODE_SERVER_USERNAME: "user",
    })

    expect(env.OPENCODE_SERVER_PASSWORD).toBeUndefined()
    expect(env.OPENCODE_SERVER_USERNAME).toBeUndefined()
  })
})

describe("runCommand onLine callback", () => {
  it("EmitsLinesFromStdoutPreservingAggregate", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "process.stdout.write('hello\\nworld\\n'); process.exit(0)",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["hello", "world"])
    expect(result.stdout).toBe("hello\nworld\n")
    expect(result.exitCode).toBe(0)
  })

  it("EmitsTrailingLineWithoutNewlineAsFinalLine", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "process.stdout.write('alpha\\nbeta'); process.exit(0)",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["alpha", "beta"])
    expect(result.stdout).toBe("alpha\nbeta")
  })

  it("MergesStdoutAndStderrThroughSingleCallback", async () => {
    const lines: string[] = []
    const result = await spawnScriptWithLineCallback(
      "process.stdout.write('out-1\\n'); process.stderr.write('err-1\\n'); process.stdout.write('out-2'); process.exit(0)",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["out-1", "err-1", "out-2"])
    expect(result.stdout).toBe("out-1\nout-2")
    expect(result.stderr).toBe("err-1\n")
  })

  it("DrainsBufferedTailAfterClose", async () => {
    const lines: string[] = []
    await spawnScriptWithLineCallback(
      "process.stderr.write('partial-tail'); process.exit(2)",
      (line) => lines.push(line),
    )

    expect(lines).toEqual(["partial-tail"])
  })

  it("EmitsOnCloseOnceAfterDrain", async () => {
    const lines: string[] = []
    const exits: number[] = []
    await runCommand(
      process.execPath,
      ["-e", "process.stdout.write('ok\\n'); process.exit(7)"],
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
      "process.stdout.write('alpha\\nbeta'); process.stderr.write('gamma\\n'); process.exit(3)",
    )

    expect(result.stdout).toBe("alpha\nbeta")
    expect(result.stderr).toBe("gamma\n")
    expect(result.exitCode).toBe(3)
  })

  it("ByteIdenticalAggregateWithAndWithoutOnLine", async () => {
    const script = "process.stdout.write('one\\ntwo\\n'); process.stderr.write('err\\n'); process.exit(0)"
    const without = await runCommand(process.execPath, ["-e", script], process.cwd(), new AbortController().signal)
    const withLines = await spawnScriptWithLineCallback(script, () => undefined)

    expect(withLines).toEqual(without)
  })

  it("HandlesCrlfBoundariesAndPreservesLineContent", async () => {
    const lines: string[] = []
    await spawnScriptWithLineCallback(
      "process.stdout.write('crlf\\r\\nlf\\n'); process.exit(0)",
      (line) => lines.push(line),
    )

    // The \r is consumed at the boundary so the line content is plain.
    expect(lines).toEqual(["crlf", "lf"])
  })
})

describe("runCommand signal handling", () => {
  it("AbortsAndPropagatesSignal", async () => {
    const controller = new AbortController()
    const promise = runCommand(
      process.execPath,
      ["-e", "setInterval(() => {}, 1000)"],
      process.cwd(),
      controller.signal,
    )
    controller.abort()
    await expect(promise).rejects.toBeTruthy()
  })
})