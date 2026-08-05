import { describe, expect, it } from "vitest"
import {
  RUNNER_LOG_MAX_BYTES,
  createRunnerLogger,
  resolveRunnerLogsDirectory,
  type LogFileWriter,
} from "./logger.js"

class FakeLogFileWriter implements LogFileWriter {
  readonly directories: string[] = []
  readonly appended: Array<{ path: string; content: string }> = []
  readonly renames: Array<{ source: string; destination: string }> = []
  readonly sizes = new Map<string, number>()

  async ensureDirectory(directory: string): Promise<void> {
    this.directories.push(directory)
  }

  async size(path: string): Promise<number> {
    return this.sizes.get(path) ?? 0
  }

  async append(path: string, content: string): Promise<void> {
    this.appended.push({ path, content })
    this.sizes.set(path, (this.sizes.get(path) ?? 0) + Buffer.byteLength(content, "utf8"))
  }

  async rename(source: string, destination: string): Promise<boolean> {
    this.renames.push({ source, destination })
    const size = this.sizes.get(source)
    if (size === undefined) return false
    this.sizes.delete(source)
    this.sizes.set(destination, size)
    return true
  }
}

describe("runner logger", () => {
  it("writes strict logfmt to the file and terminal with escaped values", async () => {
    const writer = new FakeLogFileWriter()
    const terminal: string[] = []
    const logger = createRunnerLogger({
      logsPath: "/virtual/logs",
      clock: () => new Date("2026-01-01T00:00:00.123Z"),
      fileWriter: writer,
      terminal: { write: (line) => terminal.push(line) },
    })

    logger.child("work").info("claim\nready", {
      work: "w abc",
      attempt: 3,
      reason: "bad=\"line\\next",
      ok: true,
      absent: undefined,
    })
    await logger.flush()

    const expected = "time=2026-01-01T00:00:00.123Z level=INFO msg=\"claim\\nready\" service=runner component=work work=\"w abc\" attempt=3 reason=\"bad=\\\"line\\\\next\" ok=true\n"
    expect(terminal).toEqual([expected])
    expect(writer.directories).toEqual(["/virtual/logs"])
    expect(writer.appended).toEqual([{ path: "/virtual/logs/runner.log", content: expected }])
  })

  it("drops fields colliding with fixed leading keys", async () => {
    const writer = new FakeLogFileWriter()
    const terminal: string[] = []
    const logger = createRunnerLogger({
      logsPath: "/virtual/logs",
      clock: () => new Date("2026-01-01T00:00:00.123Z"),
      fileWriter: writer,
      terminal: { write: (line) => terminal.push(line) },
    })

    logger.child("work").error("report failed", {
      time: "forged",
      level: "TRACE",
      msg: "forged",
      service: "forged",
      component: "forged",
      exception: new Error("boom"),
      work: "w_1",
    })
    await logger.flush()

    const line = terminal[0]
    expect(line).toMatch(/^time=2026-01-01T00:00:00\.123Z level=ERROR msg="report failed" service=runner component=work /)
    expect(line).toContain("work=w_1")
    expect(line).toContain('exception="Error: boom\\n')
    expect(line).not.toContain("forged")
    expect(line.match(/\btime=/g)).toHaveLength(1)
    expect(line.match(/\blevel=/g)).toHaveLength(1)
    expect(line.match(/\bmsg=/g)).toHaveLength(1)
  })

  it("keeps exception type, message, and stack on one escaped line", async () => {
    const writer = new FakeLogFileWriter()
    const error = new Error("connection refused")
    error.name = "HttpRequestException"
    error.stack = "HttpRequestException: connection refused\n   at RunnerClient.Report (...)"
    const logger = createRunnerLogger({
      logsPath: "/virtual/logs",
      clock: () => new Date("2026-01-01T00:00:00.123Z"),
      fileWriter: writer,
      terminal: { write: () => undefined },
    })

    logger.child("report").error("report failed", { work: "w_abc", attempt: 3, exception: error })
    await logger.flush()

    expect(writer.appended[0]?.content).toBe("time=2026-01-01T00:00:00.123Z level=ERROR msg=\"report failed\" service=runner component=report work=w_abc attempt=3 exception=\"HttpRequestException: connection refused\\n   at RunnerClient.Report (...)\"\n")
  })

  it("rotates the current file through two uncompressed generations", async () => {
    const writer = new FakeLogFileWriter()
    writer.sizes.set("/virtual/logs/runner.log", RUNNER_LOG_MAX_BYTES)
    const logger = createRunnerLogger({
      logsPath: "/virtual/logs",
      clock: () => new Date("2026-01-01T00:00:00.123Z"),
      fileWriter: writer,
      terminal: { write: () => undefined },
    })

    logger.info("rotated")
    await logger.flush()

    expect(writer.renames).toEqual([
      { source: "/virtual/logs/runner.log.1", destination: "/virtual/logs/runner.log.2" },
      { source: "/virtual/logs/runner.log", destination: "/virtual/logs/runner.log.1" },
    ])
    expect(writer.appended[0]?.path).toBe("/virtual/logs/runner.log")
  })

  it("resolves the configured or default log directory", () => {
    expect(resolveRunnerLogsDirectory({ MOHIST_LOGS_PATH: "/configured/logs" }, "/home/test")).toBe("/configured/logs")
    expect(resolveRunnerLogsDirectory({}, "/home/test")).toBe("/home/test/.mohist/logs")
  })
})
