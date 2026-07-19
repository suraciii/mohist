import { describe, expect, it } from "vitest"
import { createOpencodeClient } from "@opencode-ai/sdk/v2/client"
import { runTurn } from "../src/runtime/opencode/turn.js"
import type {
  RuntimeEventListener,
  RuntimeEventSubscription,
  RuntimeGlobalEvent,
} from "../src/runtime/opencode/event-subscription.js"

class FakeSubscription implements RuntimeEventSubscription {
  private readonly listeners = new Set<RuntimeEventListener>()

  subscribe(listener: RuntimeEventListener): () => void {
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

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { "content-type": "application/json" },
  })
}

describe("OpenCode SDK turn contract", () => {
  it("uses typed session parameters and confirms abort in the same directory", async () => {
    const requests: Request[] = []
    const fetch: typeof globalThis.fetch = async (input, init): Promise<Response> => {
      const request = input instanceof Request ? input : new Request(input, init)
      requests.push(request.clone())
      const url = new URL(request.url)
      if (request.method === "GET" && url.pathname === "/session/ses_contract") {
        return jsonResponse({ id: "ses_contract" })
      }
      if (request.method === "GET" && url.pathname === "/session/status") {
        return jsonResponse({})
      }
      if (request.method === "POST" && url.pathname === "/session/ses_contract/abort") {
        return jsonResponse(true)
      }
      if (request.method === "POST" && url.pathname === "/session/ses_contract/message") {
        return new Promise<Response>(() => {})
      }
      return new Response("not found", { status: 404 })
    }
    const client = createOpencodeClient({ baseUrl: "http://opencode.test", fetch })
    const events = new FakeSubscription()
    const resultPromise = runTurn({
      target: { runtime: "opencode", runtimeSessionId: "ses_contract", workDir: "/tmp/projA" },
      prompt: "do",
      options: { model: { providerID: "openai", modelID: "gpt-5" }, variant: null },
    }, { client, events }, new AbortController().signal)
    await new Promise((resolve) => setImmediate(resolve))

    events.emit({
      type: "session.status",
      sessionID: "ses_contract",
      payload: {
        sessionID: "ses_contract",
        status: { type: "retry", attempt: 1, message: "quota exceeded", next: 1000 },
      },
    })
    const result = await resultPromise

    expect(result.ok).toBe(false)
    const get = requests.find((request) => request.method === "GET" && new URL(request.url).pathname === "/session/ses_contract")
    const prompt = requests.find((request) => request.method === "POST" && new URL(request.url).pathname.endsWith("/message"))
    const abort = requests.find((request) => request.method === "POST" && new URL(request.url).pathname.endsWith("/abort"))
    expect(new URL(get!.url).searchParams.get("directory")).toBe("/tmp/projA")
    expect(new URL(prompt!.url).searchParams.get("directory")).toBe("/tmp/projA")
    expect(new URL(abort!.url).searchParams.get("directory")).toBe("/tmp/projA")
    expect(await prompt!.json()).toMatchObject({
      model: { providerID: "openai", modelID: "gpt-5" },
      parts: [{ type: "text", text: "do" }],
    })
  })

  it("uses the typed permission reply endpoint for the active Session", async () => {
    const requests: Request[] = []
    const fetch: typeof globalThis.fetch = async (input, init): Promise<Response> => {
      const request = input instanceof Request ? input : new Request(input, init)
      requests.push(request.clone())
      const url = new URL(request.url)
      if (request.method === "GET" && url.pathname === "/session/ses_permission") {
        return jsonResponse({ id: "ses_permission" })
      }
      if (request.method === "GET" && url.pathname === "/session/status") {
        return jsonResponse({})
      }
      if (request.method === "POST" && url.pathname === "/permission/perm_contract/reply") {
        return jsonResponse(true)
      }
      if (request.method === "POST" && url.pathname === "/session/ses_permission/abort") {
        return jsonResponse(true)
      }
      if (request.method === "POST" && url.pathname === "/session/ses_permission/message") {
        return new Promise<Response>(() => {})
      }
      return new Response("not found", { status: 404 })
    }
    const client = createOpencodeClient({ baseUrl: "http://opencode.test", fetch })
    const events = new FakeSubscription()
    const controller = new AbortController()
    const resultPromise = runTurn({
      target: { runtime: "opencode", runtimeSessionId: "ses_permission", workDir: "/tmp/projA" },
      prompt: "do",
    }, { client, events }, controller.signal)
    await new Promise((resolve) => setImmediate(resolve))

    events.emit({
      type: "permission.asked",
      sessionID: "ses_permission",
      directory: "/tmp/projA",
      payload: { id: "perm_contract", sessionID: "ses_permission" },
    })
    await new Promise((resolve) => setImmediate(resolve))

    const reply = requests.find((request) => new URL(request.url).pathname === "/permission/perm_contract/reply")
    expect(reply).toBeDefined()
    expect(new URL(reply!.url).searchParams.get("directory")).toBe("/tmp/projA")
    expect(await reply!.json()).toEqual({ reply: "once" })

    controller.abort()
    expect((await resultPromise).ok).toBe(false)
  })
})
