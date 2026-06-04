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
  it("finds provider errors by ACP session id", async () => {
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

  it("appends a concise provider error summary once", () => {
    const diagnostic = { sessionId: "ses_ok", summary: "Opencode provider error: 429 rate_limit_error on minimax/M3" }

    const message = appendOpencodeDiagnostic("Session liveness probe timed out {}", diagnostic)

    expect(message).toBe("Session liveness probe timed out {}\nOpencode provider error: 429 rate_limit_error on minimax/M3")
    expect(appendOpencodeDiagnostic(message, diagnostic)).toBe(message)
  })
})
