import { describe, expect, it, vi } from "vitest"
import { SlackAdapter, normalizeSocketEvent } from "./adapter.js"
import type { AdapterSession, AdapterTransport, Delivery, IngressResult, SlackConnectionRef, SlackEnvelope, SlackWebClient, SocketClient, SocketEvent } from "./types.js"

class FakeSocket implements SocketClient {
  private handler?: (event: SocketEvent) => Promise<void>
  started = false
  disconnected = false

  on(_event: "slack_event", handler: (event: SocketEvent) => Promise<void>) {
    this.handler = handler
  }

  async start() {
    this.started = true
  }

  async emit(body: unknown) {
    let acknowledged = false
    await this.handler?.({ body, ack: () => { acknowledged = true } })
    return acknowledged
  }

  async disconnect() {
    this.disconnected = true
  }
}

class FakeTransport implements AdapterTransport {
  readonly leases: SlackConnectionRef[] = []
  readonly envelopes: SlackEnvelope[] = []
  readonly acks: Array<{ ref: SlackConnectionRef; id: string; outcome: string }> = []
  readonly deliveries: Delivery[] = [{ id: "delivery-1", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: "accepted" }) }]
  connections: SlackConnectionRef[] = []
  nextIngressResults: IngressResult[] = []
  private readonly sessionByConnection = new Map<string, AdapterSession>()

  async discoverConnections(): Promise<readonly SlackConnectionRef[]> {
    return this.connections
  }

  async lease(ref: SlackConnectionRef): Promise<AdapterSession> {
    this.leases.push(ref)
    const session = { adapterId: "adapter-1", appToken: `xapp-${ref.connectionId}`, botToken: `xoxb-${ref.connectionId}` }
    this.sessionByConnection.set(ref.connectionId, session)
    return session
  }

  async ingress(_ref: SlackConnectionRef, envelope: SlackEnvelope): Promise<IngressResult> {
    this.envelopes.push(envelope)
    const queued = this.nextIngressResults.shift()
    return queued ?? { kind: "accepted" }
  }

  async claimDelivery(): Promise<Delivery | null> {
    return this.deliveries.shift() ?? null
  }

  async ackDelivery(ref: SlackConnectionRef, ack: { id: string; outcome: "delivered" | "uncertain" | "retry" }) {
    this.acks.push({ ref, id: ack.id, outcome: ack.outcome })
  }
}

class FakeWeb implements SlackWebClient {
  readonly posted: Array<{ channel: string; text: string; thread_ts?: string }> = []
  nextResponses: Array<{ ok?: boolean; error?: string }> = []
  readonly chat = {
    postMessage: async (input: { channel: string; text: string; thread_ts?: string }) => {
      this.posted.push(input)
      const next = this.nextResponses.shift()
      return next ?? { ok: true }
    },
  }
}

describe("mohist-slack adapter", () => {
  it("normalizes a Socket Mode event with stable identity", () => {
    expect(normalizeSocketEvent({
      team_id: "T1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", user: "U1", text: "do work" },
    })).toEqual({
      eventType: "message",
      isDirectMessage: true,
      teamId: "T1",
      conversationId: "D1",
      messageTs: "123.456",
      threadTs: null,
      mentionedUserIds: [],
      senderSlackUserId: "U1",
      senderKind: "human",
      text: "do work",
      files: [],
    })
  })

  it("forwards file metadata without Slack secrets or raw payload", () => {
    const envelope = normalizeSocketEvent({
      team_id: "T1",
      bot_token: "xoxb-secret",
      event: {
        type: "message",
        subtype: "file_share",
        channel: "D1",
        ts: "123.456",
        user: "U1",
        text: "read these",
        files: [
          {
            id: "F1",
            name: "report.txt",
            mimetype: "text/plain",
            size: 42,
            url_private: "https://files.slack.com/secret",
            url_private_download: "https://files.slack.com/download-secret",
          },
          {
            id: "F2",
            name: "image.png",
            mimetype: "image/png",
            size: 2048,
            permalink: "https://workspace.slack.com/files/F2",
          },
        ],
      },
    })

    expect(envelope.files).toEqual([
      { id: "F1", name: "report.txt", mimetype: "text/plain", size: 42 },
      { id: "F2", name: "image.png", mimetype: "image/png", size: 2048 },
    ])
    expect(JSON.stringify(envelope)).not.toContain("url_private")
    expect(JSON.stringify(envelope)).not.toContain("xoxb-secret")
    expect(envelope).not.toHaveProperty("event")
  })

  it("normalizes channel threads, all mentions, bot senders, and unknown senders", () => {
    expect(normalizeSocketEvent({
      team_id: "T1",
      event: {
        type: "message",
        channel: "C1",
        ts: "123.456",
        thread_ts: "123.000",
        user: "U1",
        text: "<@B1> ask <@B2|other> and <@B1> again",
      },
    })).toMatchObject({
      threadTs: "123.000",
      mentionedUserIds: ["B1", "B2"],
      senderSlackUserId: "U1",
      senderKind: "human",
    })

    expect(normalizeSocketEvent({
      team_id: "T1",
      event: { channel: "C1", ts: "123.457", subtype: "bot_message", bot_id: "B1", text: "reply" },
    })).toMatchObject({
      teamId: "T1",
      conversationId: "C1",
      messageTs: "123.457",
      senderSlackUserId: null,
      senderKind: "bot",
    })

    expect(normalizeSocketEvent({
      team_id: "T1",
      event: { channel: "C1", ts: "123.458", text: "system event" },
    })).toMatchObject({
      teamId: "T1",
      conversationId: "C1",
      messageTs: "123.458",
      senderSlackUserId: null,
      senderKind: "unknown",
    })
  })

  it("acknowledges bot and unknown events without requiring a user id", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await expect(socket.emit({
      team_id: "T1",
      event: { channel: "C1", ts: "123.457", subtype: "bot_message", bot_id: "B1", text: "reply" },
    })).resolves.toBe(true)
    await expect(socket.emit({
      team_id: "T1",
      event: { channel: "C1", ts: "123.458", text: "system event" },
    })).resolves.toBe(true)
    expect(transport.envelopes.map((envelope) => [envelope.messageTs, envelope.senderKind])).toEqual([
      ["123.457", "bot"],
      ["123.458", "unknown"],
    ])
    controller.abort()
  })

  it("discovers connections, forwards every event to ingress, and drains replies", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p1", connectionId: "c1" }, { projectId: "p1", connectionId: "c2" }]
    const sockets = new Map<string, FakeSocket>()
    const webs = new Map<string, FakeWeb>()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: (_token, ref) => {
        const socket = new FakeSocket()
        sockets.set(ref.connectionId, socket)
        return socket
      },
      webFactory: (_token, ref) => {
        const web = new FakeWeb()
        webs.set(ref.connectionId, web)
        return web
      },
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })

    const controller = new AbortController()
    await adapter.start(controller.signal)
    expect(transport.leases.map((ref) => ref.connectionId)).toEqual(["c1", "c2"])
    expect(sockets.get("c1")?.started).toBe(true)
    expect(await sockets.get("c1")?.emit({ team_id: "T1", event: { type: "message", channel: "D1", channel_type: "im", ts: "1.2", user: "U1", text: "task" } })).toBe(true)
    expect(transport.envelopes).toHaveLength(1)
    expect(transport.envelopes[0]?.text).toBe("task")
    expect(webs.get("c1")?.posted).toEqual([{ channel: "D1", text: "accepted" }])
    expect(transport.acks).toEqual([{ ref: { projectId: "p1", connectionId: "c1" }, id: "delivery-1", outcome: "delivered" }])
    controller.abort()
  })

  it("starts with zero connections and reconciles later additions and removals", async () => {
    const transport = new FakeTransport()
    const sockets = new Map<string, FakeSocket>()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      transport,
      socketFactory: (_token, ref) => {
        const socket = new FakeSocket()
        sockets.set(ref.connectionId, socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await expect(adapter.start(controller.signal)).resolves.toBeUndefined()
    expect(transport.leases).toEqual([])

    transport.connections = [{ projectId: "p", connectionId: "c" }]
    await adapter.refreshConnections(controller.signal)
    expect(sockets.get("c")?.started).toBe(true)

    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    expect(sockets.get("c")?.disconnected).toBe(true)
    controller.abort()
  })

  it("posts threaded deliveries in a thread and DM deliveries without a thread target", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.push({ id: "delivery-2", conversationId: "C1", threadTs: "1.2", payloadJson: JSON.stringify({ text: "thread reply" }) })
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    expect(web.posted).toEqual([
      { channel: "D1", text: "accepted" },
      { channel: "C1", text: "thread reply", thread_ts: "1.2" },
    ])
    controller.abort()
  })

  it("posts the backpressured reason to the originating conversation so the sender can see the refusal", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      { kind: "backpressured", reason: "This Slack Connection is backpressured; retry after pending deliveries drain." },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const acknowledged = await socket.emit({
      team_id: "T1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", thread_ts: "123.000", user: "U1", text: "do work" },
    })

    expect(acknowledged).toBe(true)
    expect(web.posted).toEqual([
      { channel: "D1", text: "This Slack Connection is backpressured; retry after pending deliveries drain.", thread_ts: "123.000" },
    ])
    expect(transport.acks).toEqual([])
    controller.abort()
  })

  it("does not render server-enqueued rejected kinds so the outbox reply is not duplicated", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      { kind: "rejected", reason: "Please send a task for the Agent to perform." },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const acknowledged = await socket.emit({
      team_id: "T1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "123.456", user: "U1", text: "" },
    })

    expect(acknowledged).toBe(true)
    expect(web.posted).toEqual([])
    controller.abort()
  })

  it("distinguishes a backpressured refusal from an accepted result that is still pending", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      { kind: "accepted" },
      { kind: "backpressured", reason: "retry shortly" },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const firstAck = await socket.emit({
      team_id: "T1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "1710000000.000001", user: "U1", text: "first" },
    })
    const secondAck = await socket.emit({
      team_id: "T1",
      event: { type: "message", channel: "D1", channel_type: "im", ts: "1710000000.000002", user: "U1", text: "second" },
    })

    expect(firstAck).toBe(true)
    expect(secondAck).toBe(true)
    expect(web.posted).toEqual([
      { channel: "D1", text: "retry shortly" },
    ])
    controller.abort()
  })

  it("acks explicit Slack rejections as retry so the same post can be re-sent without duplicating", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries = [{ id: "delivery-rejected", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: "ok?" }) }]
    transport.deliveries.push({ id: "delivery-accepted", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: "ok?" }) })
    const web = new FakeWeb()
    web.nextResponses = [
      { ok: false, error: "channel_not_found" },
      { ok: true },
    ]
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await vi.waitFor(() => expect(web.posted.length).toBe(2))
    expect(transport.acks).toEqual([
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-rejected", outcome: "retry" },
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-accepted", outcome: "delivered" },
    ])
    expect(web.posted).toEqual([
      { channel: "D1", text: "ok?" },
      { channel: "D1", text: "ok?" },
    ])
    controller.abort()
  })

  it("acks transport or payload errors as uncertain so the row is held for operator action", async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: "p", connectionId: "c" }]
    transport.deliveries = [
      { id: "delivery-bad-json", conversationId: "D1", threadTs: null, payloadJson: "not-json" },
      { id: "delivery-no-text", conversationId: "D1", threadTs: null, payloadJson: JSON.stringify({ text: null }) },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await vi.waitFor(() => expect(transport.acks.length).toBe(2))
    expect(transport.acks).toEqual([
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-bad-json", outcome: "uncertain" },
      { ref: { projectId: "p", connectionId: "c" }, id: "delivery-no-text", outcome: "uncertain" },
    ])
    expect(web.posted).toEqual([])
    controller.abort()
  })

  it("limits concurrent ingress without using a durable queue", async () => {
    const socket = new FakeSocket()
    let active = 0
    let maximum = 0
    let releaseFirst!: () => void
    const first = new Promise<void>((resolve) => { releaseFirst = resolve })
    const transport: AdapterTransport = {
      discoverConnections: async () => [{ projectId: "p", connectionId: "c" }],
      lease: async () => ({ adapterId: "a", appToken: "app", botToken: "bot" }),
      ingress: async () => {
        active += 1
        maximum = Math.max(maximum, active)
        if (active === 1) await first
        active -= 1
        return { kind: "accepted" }
      },
      claimDelivery: async () => null,
      ackDelivery: async () => undefined,
    }
    const adapter = new SlackAdapter({
      adapterId: "a",
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      maxInFlight: 1,
      deliveryPollIntervalMs: 60_000,
      heartbeatIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const event = { team_id: "T", event: { channel: "D", ts: "1", user: "U", text: "x" } }
    const firstEvent = socket.emit(event)
    await vi.waitFor(() => expect(active).toBe(1))
    const secondEvent = socket.emit({ ...event, event: { ...event.event, ts: "2" } })
    await new Promise((resolve) => setTimeout(resolve, 10))
    expect(maximum).toBe(1)
    releaseFirst()
    await Promise.all([firstEvent, secondEvent])
    controller.abort()
  })
})
