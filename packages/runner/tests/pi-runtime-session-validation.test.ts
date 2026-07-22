import { describe, expect, it, vi } from "vitest"
import { PiRuntime, type PiSdkFactory, type PiSdkSession } from "../src/runtime/pi/index.js"

class FakeSession implements PiSdkSession {
  readonly sessionFile = "/virtual/sessions/one.jsonl"
  readonly sessionId = "session-1"
  readonly messages = []
  isStreaming = false
  steerCalls: string[] = []

  subscribe(): () => void { return () => {} }
  prompt(): Promise<void> { return Promise.resolve() }
  steer(text: string): Promise<void> { this.steerCalls.push(text); return Promise.resolve() }
  abort(): Promise<void> { return Promise.resolve() }
  compact(): Promise<void> { return Promise.resolve() }
  setModel(): Promise<void> { return Promise.resolve() }
  setThinkingLevel(): void {}
  getModel(): unknown { return undefined }
  getThinkingLevel(): string { return "off" }
  dispose(): void {}
}

function factory(session: FakeSession, validateSessionFile: () => Promise<void>): PiSdkFactory {
  return {
    create: async () => ({
      catalog: async () => [],
      createSession: async () => session,
      openSession: async () => { throw new Error("cached session should not reopen") },
      validateSessionFile,
      model: () => ({}),
      close: async () => {},
    }),
  }
}

describe("PiRuntime cached session validation", () => {
  it("validates the persisted binding before using a cached streaming session", async () => {
    const session = new FakeSession()
    const validateSessionFile = vi.fn(async () => {})
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session, validateSessionFile) })
    await runtime.start()
    await runtime.createSession({ target: { runtime: "pi", runtimeSessionId: null, workDir: "/workspace" } })
    session.isStreaming = true

    await expect(runtime.followup({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "continue" })).resolves.toMatchObject({ ok: true })

    expect(validateSessionFile).toHaveBeenCalledWith(session.sessionFile, session.sessionId)
    expect(session.steerCalls).toEqual(["continue"])
  })

  it("returns a Reset hint when an idle cached session fails persisted-session validation", async () => {
    const session = new FakeSession()
    const validateSessionFile = vi.fn(async () => { throw new Error("session is corrupt") })
    const runtime = new PiRuntime({ agentDir: "/global", sdkFactory: factory(session, validateSessionFile) })
    await runtime.start()
    await runtime.createSession({ target: { runtime: "pi", runtimeSessionId: null, workDir: "/workspace" } })

    const result = await runtime.followup({ target: { runtime: "pi", runtimeSessionId: session.sessionFile, workDir: "/workspace" }, prompt: "continue" })

    expect(result).toMatchObject({ ok: false, error: { kind: "missing-session" } })
    expect(validateSessionFile).toHaveBeenCalledWith(session.sessionFile, session.sessionId)
  })
})
