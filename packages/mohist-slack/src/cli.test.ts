import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { FetchFunction } from "@slack/web-api"
import { createSlackAdapter, resolveOperatorId, resolveOperatorToken } from "./cli.js"

type SocketModeOptionsForTest = {
  appToken: string
  dispatcher?: unknown
  autoReconnectEnabled?: boolean
  clientPingTimeout?: number
}
type WebClientOptionsForTest = { fetch?: FetchFunction }
type PostMessageInput = { channel: string; text: string; thread_ts?: string }
type SocketHandler = (...args: unknown[]) => void
type SocketInstanceForTest = {
  starts: number
  emit(event: string, ...args: unknown[]): void
}

const slackSdkMocks = vi.hoisted(() => ({
  proxyUrls: [] as string[],
  socketOptions: [] as SocketModeOptionsForTest[],
  socketInstances: [] as SocketInstanceForTest[],
  proxyCloseCalls: 0,
  webOptions: [] as Array<{ token: string; fetch?: FetchFunction }>,
  undiciFetchCalls: [] as Array<{ input: string; dispatcher: unknown }>,
}))

vi.mock("@slack/socket-mode", () => ({
  SocketModeClient: class {
    readonly handlers = new Map<string, SocketHandler[]>()
    starts = 0

    constructor(options: SocketModeOptionsForTest) {
      slackSdkMocks.socketOptions.push(options)
      slackSdkMocks.socketInstances.push(this)
    }

    on(event: string, handler: SocketHandler) {
      const handlers = this.handlers.get(event) ?? []
      handlers.push(handler)
      this.handlers.set(event, handlers)
    }

    emit(event: string, ...args: unknown[]) {
      for (const handler of this.handlers.get(event) ?? []) handler(...args)
    }

    async start() {
      this.starts += 1
      this.emit("ws_message", JSON.stringify({ type: "hello", num_connections: 1, connection_info: { app_id: "A1", team_id: "T1" } }), false)
      this.emit("connected")
      return { url: "wss://socket.test" }
    }

    async disconnect() {
      this.emit("disconnected")
    }
  },
}))

vi.mock("@slack/web-api", () => ({
  WebClient: class {
    readonly chat: { postMessage: (input: PostMessageInput) => Promise<{ ok: boolean; ts: string }> }

    constructor(token: string, options?: WebClientOptionsForTest) {
      const fetch = options?.fetch
      slackSdkMocks.webOptions.push({ token, fetch })
      this.chat = {
        postMessage: async (input) => {
          if (!fetch) return { ok: true, ts: "fake.001" }
          const response = await fetch("https://slack.com/api/chat.postMessage", {
            method: "POST",
            headers: {
              authorization: `Bearer ${token}`,
              "content-type": "application/x-www-form-urlencoded",
            },
            body: new URLSearchParams({
              channel: input.channel,
              text: input.text,
              ...(input.thread_ts ? { thread_ts: input.thread_ts } : {}),
            }).toString(),
          })
          return await response.json() as { ok: boolean; ts: string }
        },
      }
    }
  },
}))

vi.mock("undici", () => ({
  ProxyAgent: class {
    constructor(url: string) {
      slackSdkMocks.proxyUrls.push(url)
    }

    async close() {
      slackSdkMocks.proxyCloseCalls += 1
    }
  },
  fetch: async (input: string | URL, init?: { dispatcher?: unknown }) => {
    slackSdkMocks.undiciFetchCalls.push({ input: String(input), dispatcher: init?.dispatcher })
    return new Response(JSON.stringify({ ok: true, ts: "proxy.001" }), { status: 200 })
  },
}))

const directToken = "direct-token"

beforeEach(() => {
  slackSdkMocks.proxyUrls.length = 0
  slackSdkMocks.socketOptions.length = 0
  slackSdkMocks.socketInstances.length = 0
  slackSdkMocks.proxyCloseCalls = 0
  slackSdkMocks.webOptions.length = 0
  slackSdkMocks.undiciFetchCalls.length = 0
})

afterEach(() => vi.useRealTimers())

function compositionServerFetch(withManagerDelivery = true): typeof fetch {
  let managerDeliveryClaimed = false
  return async (input, init) => {
    const url = String(input)
    const requestBody = JSON.parse(String(init?.body ?? "{}")) as { kind?: string; target?: { kind?: string } }
    let data: unknown
    if (url.endsWith("/api/slack-adapter/leases/targets")) {
      data = [
        { kind: "connection", projectId: "p", connectionId: "c", expectedAppId: "A1" },
        { kind: "manager", enrollmentId: "m", workspaceTeamId: "T", expectedAppId: "A2" },
      ]
    } else if (url.endsWith("/api/slack-adapter/leases/acquire")) {
      if (requestBody.kind === "validation") {
        data = null
      } else if (requestBody.target?.kind === "manager") {
        data = { leaseId: "lease-m", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp-m", botToken: "xoxb-manager" }
      } else {
        data = { leaseId: "lease-c", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp-c", botToken: "xoxb-c" }
      }
    } else if (url.endsWith("/deliveries/claim-uncertain")) {
      data = null
    } else if (url.endsWith("/deliveries/claim")) {
      const isManager = url.includes("/api/slack-manager/")
      data = isManager && withManagerDelivery && !managerDeliveryClaimed
        ? {
            id: "delivery-1",
            ownerKind: "manager",
            conversationId: "D1",
            threadTs: null,
            payloadJson: JSON.stringify({ text: "proxy composition" }),
          }
        : null
      managerDeliveryClaimed = managerDeliveryClaimed || isManager
    } else if (url.endsWith("/deliveries/ack")) {
      data = null
    } else {
      throw new Error(`Unexpected Server request: ${url}`)
    }
    return new Response(JSON.stringify({ success: true, data }), { status: 200 })
  }
}

describe("mohist-slack CLI composition", () => {
  it("prefers a direct Mohist token over the compatibility environment variable and file path", async () => {
    const token = await resolveOperatorToken(
      {
        MOHIST_OPERATOR_TOKEN: `  ${directToken}  `,
        OPERATOR_TOKEN: "compatibility-operator-token-0123456789abcdef",
        MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token",
      },
      async () => {
        throw new Error("file reader should not be called")
      },
    )

    expect(token).toBe(directToken)
  })

  it("reads, trims, and validates a protected credential path when no direct token is present", async () => {
    const token = await resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token" },
      async (path) => {
        expect(path).toBe("/run/credentials/operator-token")
        return `\n${directToken}\n`
      },
    )

    expect(token).toBe(directToken)
  })

  it("rejects an absent credential path without exposing details", async () => {
    await expect(resolveOperatorToken({}, async () => "should not be read"))
      .rejects.toThrow("Mohist operator credential is required")
  })

  it("rejects a blank credential path without exposing details", async () => {
    await expect(resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "   " },
      async () => "should not be read",
    )).rejects.toThrow("Mohist operator credential is required")
  })

  it("rejects an unreadable credential file without exposing the filesystem error", async () => {
    const result = await resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token" },
      async () => { throw new Error("sensitive filesystem detail") },
    ).catch((error: unknown) => error as Error)

    expect(result.message).toBe("Mohist operator credential file could not be read")
    expect(result.message).not.toContain("sensitive filesystem detail")
  })

  it("rejects a blank credential file without exposing its contents", async () => {
    const result = await resolveOperatorToken(
      { MOHIST_OPERATOR_TOKEN_PATH: "/run/credentials/operator-token" },
      async () => " \n\t",
    ).catch((error: unknown) => error as Error)

    expect(result.message).toBe("Mohist operator credential is invalid")
  })

  it("resolves the operator identity from MOHIST_OPERATOR_ID, trimmed", () => {
    expect(resolveOperatorId({ MOHIST_OPERATOR_ID: "  host-operator  " })).toBe("host-operator")
  })

  it("falls back to the stable mohist-slack identity when the variable is unset", () => {
    expect(resolveOperatorId({})).toBe("mohist-slack")
  })

  it("falls back to the stable identity when the variable is blank", () => {
    expect(resolveOperatorId({ MOHIST_OPERATOR_ID: "   " })).toBe("mohist-slack")
  })

  it("delivers a Manager message through the production WebClient and acknowledges it on the Manager route", async () => {
    const managerCredential = "test-manager-credential"
    const manager = { kind: "manager" as const, enrollmentId: "manager-enrollment", workspaceTeamId: "T_MANAGER" }
    const delivery = {
      id: "manager-delivery",
      ownerKind: "manager" as const,
      conversationId: "D_MANAGER",
      threadTs: null,
      payloadJson: JSON.stringify({ text: "manager reply" }),
    }
    const serverCalls: Array<{ url: string; body?: string; operatorToken: string | null; operatorId: string | null }> = []
    const ackBodies: unknown[] = []
    let deliveryClaimed = false
    const serverFetch: typeof fetch = async (input, init) => {
      const url = String(input)
      const body = init?.body === undefined ? undefined : String(init.body)
      serverCalls.push({
        url,
        body,
        operatorToken: new Headers(init?.headers).get("x-mohist-operator-token"),
        operatorId: new Headers(init?.headers).get("x-mohist-operator-id"),
      })
      let data: unknown
      if (url.endsWith("/api/slack-adapter/leases/targets")) {
        data = [manager]
      } else if (url.endsWith("/api/slack-adapter/leases/acquire")) {
        data = JSON.parse(body ?? "{}").kind === "validation"
          ? null
          : { leaseId: "lease-manager", generation: 1, expiresAt: "2026-01-01T00:05:00Z", appToken: "xapp-manager", botToken: managerCredential }
      } else if (url.endsWith("/deliveries/claim-uncertain")) {
        data = null
      } else if (url.endsWith("/deliveries/claim")) {
        data = deliveryClaimed ? null : delivery
        deliveryClaimed = true
      } else if (url.endsWith("/deliveries/ack")) {
        ackBodies.push(JSON.parse(body ?? "null") as unknown)
        data = null
      } else {
        throw new Error(`Unexpected Server request: ${url}`)
      }
      return new Response(JSON.stringify({ success: true, data }), { status: 200 })
    }
    const slackCalls: Array<{ url: string; authorization: string | null; body: URLSearchParams }> = []
    const slackFetch: FetchFunction = async (input, init) => {
      const body = new URLSearchParams(String(init?.body ?? ""))
      slackCalls.push({
        url: String(input),
        authorization: new Headers(init?.headers).get("authorization"),
        body,
      })
      return new Response(JSON.stringify({ ok: true, ts: "1700000000.001" }), { status: 200 })
    }
    const controller = new AbortController()
    const adapter = createSlackAdapter({
      adapterId: "adapter-manager",
      serverUrl: "http://localhost",
      operatorToken: "test-operator",
      operatorId: "mohist-slack",
      serverFetch,
      slackFetch,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(controller.signal)

    expect(serverCalls.length).toBeGreaterThan(0)
    expect(serverCalls.every((call) => call.operatorToken === "test-operator")).toBe(true)
    expect(serverCalls.every((call) => call.operatorId === "mohist-slack")).toBe(true)
    expect(slackCalls).toHaveLength(1)
    expect(slackCalls[0]).toMatchObject({
      url: "https://slack.com/api/chat.postMessage",
      authorization: `Bearer ${managerCredential}`,
    })
    expect(slackCalls[0]?.body.get("channel")).toBe("D_MANAGER")
    expect(slackCalls[0]?.body.get("text")).toBe("manager reply")
    expect(ackBodies).toEqual([{
      id: delivery.id,
      outcome: "delivered",
      adapterId: "adapter-manager",
      leaseId: "lease-manager",
      providerMessageIdentity: { conversationId: "D_MANAGER", messageTs: "1700000000.001" },
    }])
    expect(serverCalls.some((call) => call.url.endsWith("/api/slack-manager/adapter/manager-enrollment/deliveries/ack"))).toBe(true)
    controller.abort()
    await adapter.stop()
    expect(slackSdkMocks.proxyCloseCalls).toBe(0)
  })

  it("injects one explicit ProxyAgent dispatcher into Socket Mode and Web API fetch", async () => {
    const controller = new AbortController()
    const adapter = createSlackAdapter({
      adapterId: "adapter-proxy",
      serverUrl: "http://localhost",
      operatorToken: "test-operator",
      operatorId: "mohist-slack",
      slackProxyUrl: "http://proxy.test:3128",
      serverFetch: compositionServerFetch(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(controller.signal)

    const dispatcher = slackSdkMocks.socketOptions[0]?.dispatcher
    const webFetches = slackSdkMocks.webOptions.map((options) => options.fetch).filter((fetch): fetch is FetchFunction => fetch !== undefined)
    expect(slackSdkMocks.proxyUrls).toEqual(["http://proxy.test:3128"])
    expect(dispatcher).toBeDefined()
    expect(slackSdkMocks.socketOptions[0]).toMatchObject({
      appToken: "xapp-c",
      dispatcher,
      autoReconnectEnabled: false,
      clientPingTimeout: 86_400_000,
    })
    expect(webFetches).toHaveLength(2)
    expect(new Set(webFetches).size).toBe(1)
    expect(slackSdkMocks.undiciFetchCalls).toEqual([{ input: "https://slack.com/api/chat.postMessage", dispatcher }])
    controller.abort()
    await adapter.stop()
    expect(slackSdkMocks.proxyCloseCalls).toBe(1)
  })

  it("reconnects a proxied Socket client with bounded adapter-owned backoff", async () => {
    vi.useFakeTimers()
    const controller = new AbortController()
    const adapter = createSlackAdapter({
      adapterId: "adapter-proxy-reconnect",
      serverUrl: "http://localhost",
      operatorToken: "test-operator",
      operatorId: "mohist-slack",
      slackProxyUrl: "http://proxy.test:3128",
      serverFetch: compositionServerFetch(false),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(controller.signal)
    const socket = slackSdkMocks.socketInstances[0]
    expect(socket?.starts).toBe(1)

    socket?.emit("disconnected")
    await vi.advanceTimersByTimeAsync(999)
    expect(socket?.starts).toBe(1)
    await vi.advanceTimersByTimeAsync(1)
    expect(socket?.starts).toBe(2)

    controller.abort()
    await adapter.stop()
    expect(slackSdkMocks.proxyCloseCalls).toBe(1)
  })

  it("keeps both Slack SDK paths on their direct defaults when no proxy URL is supplied", async () => {
    const controller = new AbortController()
    const adapter = createSlackAdapter({
      adapterId: "adapter-direct",
      serverUrl: "http://localhost",
      operatorToken: "test-operator",
      operatorId: "mohist-slack",
      serverFetch: compositionServerFetch(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(controller.signal)

    expect(slackSdkMocks.proxyUrls).toEqual([])
    expect(slackSdkMocks.socketOptions).toEqual([
      { appToken: "xapp-c", dispatcher: undefined, autoReconnectEnabled: false },
      { appToken: "xapp-m", dispatcher: undefined, autoReconnectEnabled: false },
    ])
    expect(slackSdkMocks.webOptions).toHaveLength(2)
    expect(slackSdkMocks.webOptions.every((options) => options.fetch === undefined)).toBe(true)
    expect(slackSdkMocks.undiciFetchCalls).toEqual([])
    controller.abort()
    await adapter.stop()
    expect(slackSdkMocks.proxyCloseCalls).toBe(0)
  })
})
