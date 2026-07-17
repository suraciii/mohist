import { join } from "node:path"
import { afterEach, describe, expect, it, vi } from "vitest"
import {
  appendOpencodeDiagnostic,
  findFailFastOpencodeProviderErrorDiagnostic,
  findOpencodeProviderErrorDiagnostic,
  isFailFastOpencodeProviderError,
  setOpencodeLogFileSystemForTest,
  type OpencodeLogFileSystem,
} from "../src/runtime/opencode-log-diagnostics.js"

const LOG_DIR = "/fake/opencode/log"

afterEach(() => {
  vi.unstubAllEnvs()
  setOpencodeLogFileSystemForTest(null)
})

describe("opencode log diagnostics", () => {
  it("finds provider errors by ACP session id (legacy JSON format)", async () => {
    useOpencodeLogs({
      "2026-06-03T164901.log": [
        "INFO  2026-06-03T16:49:05 service=session id=ses_ok created",
        'ERROR 2026-06-03T16:49:06 service=llm providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=ses_ok small=false agent=build mode=primary error={"error":{"name":"AI_APICallError","statusCode":429,"responseBody":"{\\"type\\":\\"error\\",\\"error\\":{\\"type\\":\\"rate_limit_error\\",\\"message\\":\\"usage limit exceeded\\"}}","isRetryable":true}} stream error',
        "",
      ].join("\n"),
    })

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_ok")

    expect(diagnostic?.summary).toContain("Opencode provider error: 429 rate_limit_error")
    expect(diagnostic?.summary).toContain("minimax-coding-plan/MiniMax-M3")
    expect(diagnostic?.summary).toContain("usage limit exceeded")
    expect(diagnostic?.statusCode).toBe(429)
    expect(diagnostic?.errorType).toBe("rate_limit_error")
    expect(diagnostic?.retryable).toBe(true)
  })

  it("does not attribute errors from other sessions sharing the same run= id", async () => {
    const trackedSession = "ses_13a627d5fffesOoW3p6ceeWkpu"
    const otherSession = "ses_13a61b7f2ffeqyUCDQE7AUFtS7"
    useOpencodeLogs({
      "opencode.log": [
        `timestamp=2026-06-14T10:11:34.688Z level=INFO run=cd59405c message=created id=${trackedSession} slug=witty-engine version=1.17.4 projectID=abc directory=/tmp agent=build model.id=k2p6 model.providerID=kimi-for-coding`,
        `timestamp=2026-06-14T10:12:25.229Z level=INFO run=cd59405c message=created id=${otherSession} slug=witty-comet version=1.17.4 projectID=abc directory=/tmp agent=build model.id=k2p6 model.providerID=kimi-for-coding`,
        `timestamp=2026-06-14T10:12:32.370Z level=ERROR run=cd59405c message="stream error" providerID=kimi-for-coding modelID=k2p7 session.id=${otherSession} small=false agent=build mode=primary error.error="AI_APICallError: You've reached your usage limit for this period."`,
        "",
      ].join("\n"),
    })

    const diagnostic = await findOpencodeProviderErrorDiagnostic(trackedSession)

    expect(diagnostic).toBeUndefined()
  })

  it("finds provider errors by exact session id (logfmt format)", async () => {
    useOpencodeLogs({
      "opencode.log": [
        `timestamp=2026-06-14T10:12:25.229Z level=INFO run=cd59405c message=created id=ses_direct slug=witty-comet version=1.17.4 agent=build`,
        `timestamp=2026-06-14T10:12:32.370Z level=ERROR run=cd59405c message="stream error" providerID=kimi-for-coding modelID=k2p7 session.id=ses_direct small=false agent=build mode=primary error.error="AI_RetryError: Failed after 3 attempts. Last error: rate limited"`,
        "",
      ].join("\n"),
    })

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_direct")

    expect(diagnostic?.errorName).toBe("AI_RetryError")
    expect(diagnostic?.message).toContain("Failed after 3 attempts")
  })

  it("finds fail-fast token plan errors after the prompt start time", async () => {
    useOpencodeLogs({
      "opencode.log": [
        `timestamp=2026-06-14T10:12:30.000Z level=ERROR run=old message="stream error" providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=ses_limit small=false agent=build mode=primary error.error="AI_APICallError: Cannot connect to API: The socket connection was closed unexpectedly."`,
        `timestamp=2026-06-14T10:12:32.370Z level=ERROR run=new message="stream error" providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=ses_limit small=false agent=build mode=primary error.error="AI_APICallError: Token Plan usage limit reached: Upgrade your Token Plan or purchase Credits for more usage. (2056)"`,
        "",
      ].join("\n"),
    })

    const diagnostic = await findFailFastOpencodeProviderErrorDiagnostic("ses_limit", Date.parse("2026-06-14T10:12:31.000Z"))

    expect(diagnostic?.summary).toBe("Opencode provider error: AI_APICallError on minimax-coding-plan/MiniMax-M3 - Token Plan usage limit reached: Upgrade your Token Plan or purchase Credits for more usage. (2056)")
  })

  it("ignores fail-fast provider errors from before the prompt start time", async () => {
    useOpencodeLogs({
      "opencode.log": [
        `timestamp=2026-06-14T10:12:30.000Z level=ERROR run=old message="stream error" providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=ses_limit small=false agent=build mode=primary error.error="AI_APICallError: Token Plan usage limit reached: Upgrade your Token Plan or purchase Credits for more usage. (2056)"`,
        "",
      ].join("\n"),
    })

    const diagnostic = await findFailFastOpencodeProviderErrorDiagnostic("ses_limit", Date.parse("2026-06-14T10:12:31.000Z"))

    expect(diagnostic).toBeUndefined()
  })

  it("does not classify socket disconnects as fail-fast provider errors", () => {
    expect(isFailFastOpencodeProviderError({
      sessionId: "ses_socket",
      summary: "Opencode provider error: AI_APICallError on minimax/M3 - Cannot connect to API: The socket connection was closed unexpectedly.",
      errorName: "AI_APICallError",
      message: "Cannot connect to API: The socket connection was closed unexpectedly.",
    })).toBe(false)
  })

  it("does not classify context-overflow errors as fail-fast (opencode auto-recovers via compaction)", () => {
    expect(isFailFastOpencodeProviderError({
      sessionId: "ses_overflow",
      summary: "Opencode provider error: AI_APICallError on openai/gpt-5.6-terra - Your input exceeds the context window of this model. Please adjust your input and try again.",
      errorName: "AI_APICallError",
      message: "Your input exceeds the context window of this model. Please adjust your input and try again.",
    })).toBe(false)
  })

  it("reads tail of large log files", async () => {
    const largePrefix = `${"x".repeat(11 * 1024 * 1024)}\n`
    const errorLine = `timestamp=2026-06-14T10:12:32.370Z level=ERROR run=big message="stream error" providerID=kimi-for-coding modelID=k2p7 session.id=ses_big small=false agent=build mode=primary error.error="AI_APICallError: quota exceeded"\n`
    useOpencodeLogs({ "opencode.log": largePrefix + errorLine })

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_big")

    expect(diagnostic?.message).toContain("quota exceeded")
  })

  it("returns undefined when no provider errors exist", async () => {
    useOpencodeLogs({
      "opencode.log": [
        `timestamp=2026-06-14T10:11:34.688Z level=INFO run=cd59405c message=created id=ses_clean slug=witty-engine agent=build`,
        `timestamp=2026-06-14T10:12:23.285Z level=INFO run=cd59405c message="exiting loop" session.id=ses_clean`,
        "",
      ].join("\n"),
    })

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_clean")

    expect(diagnostic).toBeUndefined()
  })

  it("appends a concise provider error summary once", () => {
    const diagnostic = { sessionId: "ses_ok", summary: "Opencode provider error: 429 rate_limit_error on minimax/M3" }

    const message = appendOpencodeDiagnostic("Session liveness probe timed out {}", diagnostic)

    expect(message).toBe("Session liveness probe timed out {}\nOpencode provider error: 429 rate_limit_error on minimax/M3")
    expect(appendOpencodeDiagnostic(message, diagnostic)).toBe(message)
  })
})

function useOpencodeLogs(files: Record<string, string>) {
  vi.stubEnv("MOHIST_OPENCODE_LOG_DIR", LOG_DIR)
  const entries = Object.entries(files)
  const fileSystem: OpencodeLogFileSystem = {
    async readdir(path) {
      if (path !== LOG_DIR) throw new Error(`unexpected log dir: ${path}`)
      return entries.map(([name]) => name)
    },
    async stat(path) {
      const text = fileText(path)
      const index = entries.findIndex(([name]) => join(LOG_DIR, name) === path)
      return {
        isFile: () => true,
        mtimeMs: index,
        size: Buffer.byteLength(text),
      }
    },
    async readFile(path) {
      return fileText(path)
    },
    async readTail(path, start, length) {
      return fileText(path).slice(start, start + length)
    },
  }
  setOpencodeLogFileSystemForTest(fileSystem)

  function fileText(path: string) {
    const name = path.slice(LOG_DIR.length + 1)
    if (!(name in files)) throw new Error(`unexpected log file: ${path}`)
    return files[name]
  }
}
