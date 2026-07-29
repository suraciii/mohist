import { describe, expect, it, vi } from "vitest"
import { SlackAdapter, normalizeSocketEvent } from "./adapter.js"
import type { AdapterSession, AdapterTransport, Delivery, IngressResult, SlackConnectionRef, SlackEnvelope, SlackWebClient, SocketClient, SocketEvent } from "./types.js"

class FakeSocket implements SocketClient {
  private handler?: (event: SocketEvent) => Promise<void>
  started = false

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
}

class FakeTransport implements AdapterTransport {
  readonly leases: SlackConnectionRef[] = []
  readonly envelopes: SlackEnvelope[] = []
  readonly acks: Array<{ ref: SlackConnectionRef; id: string; outcome: string }> = []
  readonly deliveries: Delivery[] = [{ id: "delivery-1", dmConversationId: "D1", payloadJson: JSON.stringify({ text: "accepted" }) }]
  private readonly sessionByConnection = new Map<string, AdapterSession>()

  async lease(ref: SlackConnectionRef): Promise<AdapterSession> {
    this.leases.push(ref)
    const session = { adapterId: "adapter-1", appToken: `xapp-${ref.connectionId}`, botToken: `xoxb-${ref.connectionId}` }
    this.sessionByConnection.set(ref.connectionId, session)
    return session
  }

  async ingress(_ref: SlackConnectionRef, envelope: SlackEnvelope): Promise<IngressResult> {
    this.envelopes.push(envelope)
    return { kind: "accepted" }
  }

  async claimDelivery(): Promise<Delivery | null> {
    return this.deliveries.shift() ?? null
  }

  async ackDelivery(ref: SlackConnectionRef, ack: { id: string; outcome: "delivered" | "uncertain" | "retry" }) {
    this.acks.push({ ref, id: ack.id, outcome: ack.outcome })
  }
}

class FakeWeb implements SlackWebClient {
  readonly posted: Array<{ channel: string; text: string }> = []
  readonly chat = {
    postMessage: async (input: { channel: string; text: string }) => {
      this.posted.push(input)
      return { ok: true }
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
      senderSlackUserId: "U1",
      text: "do work",
    })
  })

  it("leases each connection, forwards every event to ingress, and drains replies", async () => {
    const transport = new FakeTransport()
    const sockets = new Map<string, FakeSocket>()
    const webs = new Map<string, FakeWeb>()
    const adapter = new SlackAdapter({
      adapterId: "adapter-1",
      connections: [{ projectId: "p1", connectionId: "c1" }, { projectId: "p1", connectionId: "c2" }],
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

  it("limits concurrent ingress without using a durable queue", async () => {
    const socket = new FakeSocket()
    let active = 0
    let maximum = 0
    let releaseFirst!: () => void
    const first = new Promise<void>((resolve) => { releaseFirst = resolve })
    const transport: AdapterTransport = {
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
      connections: [{ projectId: "p", connectionId: "c" }],
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
