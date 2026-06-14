import { mkdtemp, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { afterEach, describe, expect, it } from "vitest"
import { appendOpencodeDiagnostic, findOpencodeProviderErrorDiagnostic } from "../src/runtime/opencode-log-diagnostics.js"

let tempDir: string | undefined

afterEach(async () => {
  delete process.env.MOHIST_OPENCODE_LOG_DIR
  if (tempDir) await rm(tempDir, { recursive: true, force: true })
  tempDir = undefined
})

describe("opencode log diagnostics", () => {
  it("finds provider errors by ACP session id (legacy JSON format)", async () => {
    tempDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = tempDir
    await writeFile(join(tempDir, "2026-06-03T164901.log"), [
      "INFO  2026-06-03T16:49:05 service=session id=ses_ok created",
      'ERROR 2026-06-03T16:49:06 service=llm providerID=minimax-coding-plan modelID=MiniMax-M3 session.id=ses_ok small=false agent=build mode=primary error={"error":{"name":"AI_APICallError","statusCode":429,"responseBody":"{\\"type\\":\\"error\\",\\"error\\":{\\"type\\":\\"rate_limit_error\\",\\"message\\":\\"usage limit exceeded\\"}}","isRetryable":true}} stream error',
      "",
    ].join("\n"))

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_ok")

    expect(diagnostic?.summary).toContain("Opencode provider error: 429 rate_limit_error")
    expect(diagnostic?.summary).toContain("minimax-coding-plan/MiniMax-M3")
    expect(diagnostic?.summary).toContain("usage limit exceeded")
    expect(diagnostic?.statusCode).toBe(429)
    expect(diagnostic?.errorType).toBe("rate_limit_error")
    expect(diagnostic?.retryable).toBe(true)
  })

  it("finds provider errors logged under a different internal session (logfmt format)", async () => {
    tempDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = tempDir
    const trackedSession = "ses_13a627d5fffesOoW3p6ceeWkpu"
    const internalSession = "ses_13a61b7f2ffeqyUCDQE7AUFtS7"
    await writeFile(join(tempDir, "opencode.log"), [
      `timestamp=2026-06-14T10:11:34.688Z level=INFO run=cd59405c message=created id=${trackedSession} slug=witty-engine version=1.17.4 projectID=abc directory=/tmp agent=build model.id=k2p6 model.providerID=kimi-for-coding`,
      `timestamp=2026-06-14T10:12:25.229Z level=INFO run=cd59405c message=created id=${internalSession} slug=witty-comet version=1.17.4 projectID=abc directory=/tmp agent=build model.id=k2p6 model.providerID=kimi-for-coding`,
      `timestamp=2026-06-14T10:12:32.370Z level=ERROR run=cd59405c message="stream error" providerID=kimi-for-coding modelID=k2p7 session.id=${internalSession} small=false agent=build mode=primary error.error="AI_APICallError: You've reached your usage limit for this period. Your quota will be refreshed in the next period. Upgrade to get more: https://www.kimi.com/code/console?from=limit-upgrade"`,
      "",
    ].join("\n"))

    const diagnostic = await findOpencodeProviderErrorDiagnostic(trackedSession)

    expect(diagnostic).toBeDefined()
    expect(diagnostic?.providerId).toBe("kimi-for-coding")
    expect(diagnostic?.modelId).toBe("k2p7")
    expect(diagnostic?.errorName).toBe("AI_APICallError")
    expect(diagnostic?.message).toContain("usage limit")
    expect(diagnostic?.summary).toContain("kimi-for-coding/k2p7")
    expect(diagnostic?.summary).toContain("AI_APICallError")
    expect(diagnostic?.summary).toContain("usage limit")
  })

  it("finds provider errors by exact session id (logfmt format)", async () => {
    tempDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = tempDir
    await writeFile(join(tempDir, "opencode.log"), [
      `timestamp=2026-06-14T10:12:25.229Z level=INFO run=cd59405c message=created id=ses_direct slug=witty-comet version=1.17.4 agent=build`,
      `timestamp=2026-06-14T10:12:32.370Z level=ERROR run=cd59405c message="stream error" providerID=kimi-for-coding modelID=k2p7 session.id=ses_direct small=false agent=build mode=primary error.error="AI_RetryError: Failed after 3 attempts. Last error: rate limited"`,
      "",
    ].join("\n"))

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_direct")

    expect(diagnostic?.errorName).toBe("AI_RetryError")
    expect(diagnostic?.message).toContain("Failed after 3 attempts")
  })

  it("reads tail of large log files", async () => {
    tempDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = tempDir
    const padding = "x".repeat(100)
    const lines: string[] = []
    for (let i = 0; i < 200_000; i++) lines.push(`timestamp=2026-06-14T10:00:${String(i % 60).padStart(2, "0")}.000Z level=INFO run=big message=loop step=${i} ${padding}`)
    lines.push(`timestamp=2026-06-14T10:12:32.370Z level=ERROR run=big message="stream error" providerID=kimi-for-coding modelID=k2p7 session.id=ses_big small=false agent=build mode=primary error.error="AI_APICallError: quota exceeded"`)
    lines.push("")
    await writeFile(join(tempDir, "opencode.log"), lines.join("\n"))

    const diagnostic = await findOpencodeProviderErrorDiagnostic("ses_big")

    expect(diagnostic?.message).toContain("quota exceeded")
  })

  it("returns undefined when no provider errors exist", async () => {
    tempDir = await mkdtemp(join(tmpdir(), "mohist-opencode-log-"))
    process.env.MOHIST_OPENCODE_LOG_DIR = tempDir
    await writeFile(join(tempDir, "opencode.log"), [
      `timestamp=2026-06-14T10:11:34.688Z level=INFO run=cd59405c message=created id=ses_clean slug=witty-engine agent=build`,
      `timestamp=2026-06-14T10:12:23.285Z level=INFO run=cd59405c message="exiting loop" session.id=ses_clean`,
      "",
    ].join("\n"))

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
