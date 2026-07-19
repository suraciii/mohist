import { describe, expect, it, vi } from "vitest"
import { runTurn } from "../src/runtime/opencode/turn.js"
import type { RuntimeEventSubscription, RuntimeGlobalEvent } from "../src/runtime/opencode/event-subscription.js"
import type { OpencodeClient } from "@opencode-ai/sdk/v2"

const SESSION_ID = "ses_permission"
const DIRECTORY = "/tmp/projA"

class FakeSubscription implements RuntimeEventSubscription {
  private listeners = new Set<(event: RuntimeGlobalEvent) => void>()

  subscribe(listener: (event: RuntimeGlobalEvent) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  emit(event: RuntimeGlobalEvent): void {
    for (const listener of [...this.listeners]) listener(event)
  }

  async close(): Promise<void> {
    this.listeners.clear()
  }
}

function buildTurn() {
  const subscription = new FakeSubscription()
  const sessionCreate = vi.fn(async () => ({ data: { id: SESSION_ID } }))
  const sessionPrompt = vi.fn(async (): Promise<unknown> => ({ data: { parts: [] } }))
  const sessionAbort = vi.fn(async () => ({ data: true }))
  const sessionStatus = vi.fn(async () => ({ data: {} }))
  const permissionReply = vi.fn(async () => ({ data: true }))
  const client = {
    session: {
      create: sessionCreate,
      prompt: sessionPrompt,
      abort: sessionAbort,
      status: sessionStatus,
    },
    permission: { reply: permissionReply },
  } as unknown as OpencodeClient

  return { client, subscription, sessionPrompt, sessionAbort, permissionReply }
}

function startTurn(input: ReturnType<typeof buildTurn>) {
  return runTurn({
    target: { runtime: "opencode", runtimeSessionId: null, workDir: DIRECTORY },
    prompt: "do",
  }, { client: input.client, events: input.subscription }, new AbortController().signal)
}

describe("OpenCodeRuntime turn permissions", () => {
  it("replies once to the current Session permission request without persisting a rule", async () => {
    const turn = buildTurn()
    let resolvePrompt: (value: unknown) => void = () => {}
    turn.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
      resolvePrompt = resolve
    }))

    const resultPromise = startTurn(turn)
    await new Promise((resolve) => setImmediate(resolve))
    const permission = {
      type: "permission.asked",
      sessionID: SESSION_ID,
      directory: DIRECTORY,
      payload: { id: "perm_1", sessionID: SESSION_ID },
    } satisfies RuntimeGlobalEvent
    turn.subscription.emit(permission)
    turn.subscription.emit(permission)
    await new Promise((resolve) => setImmediate(resolve))

    expect(turn.permissionReply).toHaveBeenCalledTimes(1)
    expect(turn.permissionReply).toHaveBeenCalledWith({
      requestID: "perm_1",
      directory: DIRECTORY,
      reply: "once",
    }, { throwOnError: true })

    resolvePrompt({ data: { parts: [{ type: "text", text: "completed" }] } })
    expect((await resultPromise).ok).toBe(true)
  })

  it("ignores permission requests for another Session or work directory", async () => {
    const turn = buildTurn()
    let resolvePrompt: (value: unknown) => void = () => {}
    turn.sessionPrompt.mockImplementationOnce(() => new Promise((resolve) => {
      resolvePrompt = resolve
    }))

    const resultPromise = startTurn(turn)
    await new Promise((resolve) => setImmediate(resolve))
    turn.subscription.emit({
      type: "permission.asked",
      sessionID: "ses_other",
      directory: DIRECTORY,
      payload: { id: "perm_other", sessionID: "ses_other" },
    })
    turn.subscription.emit({
      type: "permission.asked",
      sessionID: SESSION_ID,
      directory: "/tmp/other-project",
      payload: { id: "perm_other_directory", sessionID: SESSION_ID },
    })
    await new Promise((resolve) => setImmediate(resolve))

    expect(turn.permissionReply).not.toHaveBeenCalled()
    resolvePrompt({ data: { parts: [{ type: "text", text: "completed" }] } })
    expect((await resultPromise).ok).toBe(true)
  })

  it("fails immediately with permission-required when the once reply cannot be confirmed", async () => {
    const turn = buildTurn()
    turn.sessionPrompt.mockImplementationOnce(() => new Promise(() => {}))
    turn.permissionReply.mockResolvedValueOnce({ data: false })

    const resultPromise = startTurn(turn)
    await new Promise((resolve) => setImmediate(resolve))
    turn.subscription.emit({
      type: "permission.asked",
      sessionID: SESSION_ID,
      directory: DIRECTORY,
      payload: { id: "perm_failure", sessionID: SESSION_ID },
    })

    const result = await resultPromise
    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.error.kind).toBe("permission-required")
    expect(result.error.diagnostics.some((d) => d.code === "permission-reply-failed")).toBe(true)
    expect(turn.sessionAbort).toHaveBeenCalledTimes(1)
  })

  it("leaves an explicit OpenCode denial untouched", async () => {
    const turn = buildTurn()
    turn.sessionPrompt.mockRejectedValueOnce(new Error("permission denied by OpenCode"))

    const result = await startTurn(turn)

    expect(result.ok).toBe(false)
    expect(turn.permissionReply).not.toHaveBeenCalled()
  })
})
