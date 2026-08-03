import { describe, expect, it } from "vitest"
import { SlackAdapter } from "./adapter.js"
import type { AdapterSession, AdapterTransport, Delivery, DeliveryAck, SlackConnectionRef, SlackEnvelope, SlackInteractionEnvelope, SlackWebClient, SocketClient, SocketEvent } from "./types.js"

class Socket implements SocketClient {
  private handler?: (event: SocketEvent) => Promise<void>
  on(_event: "slack_event", handler: (event: SocketEvent) => Promise<void>) { this.handler = handler }
  async start() {}
  async emit() { await this.handler?.({ body: {}, ack: () => undefined }) }
}

class Transport implements AdapterTransport {
  readonly ref: SlackConnectionRef = { projectId: "p", connectionId: "c" }
  readonly deliveries: Delivery[] = []
  readonly uncertain: Delivery[] = []
  readonly acks: DeliveryAck[] = []
  async discoverConnections() { return [this.ref] }
  async lease(): Promise<AdapterSession> { return { adapterId: "a", appToken: "app", botToken: "bot" } }
  async ingress(_ref: SlackConnectionRef, _envelope: SlackEnvelope) { return { kind: "accepted" as const } }
  async interaction(_ref: SlackConnectionRef, _envelope: SlackInteractionEnvelope) { return { state: "stop_requested" } }
  async claimDelivery() { return this.deliveries.shift() ?? null }
  async claimUncertainDelivery() { return this.uncertain.shift() ?? null }
  async ackDelivery(_ref: SlackConnectionRef, ack: DeliveryAck) { this.acks.push(ack) }
}

class Web implements SlackWebClient {
  readonly updated: Array<{ channel: string; ts: string; text: string; blocks?: readonly Record<string, unknown>[] }> = []
  readonly posted: Array<{ channel: string; text: string; thread_ts?: string; client_msg_id?: string; blocks?: readonly Record<string, unknown>[] }> = []
  reactionError: string | undefined
  reactionThrow: unknown = undefined
  reactionPresent = false
  reactionReadThrow: unknown = undefined
  updateError: string | undefined
  historyMessages: Array<{ ts?: string; client_msg_id?: string; text?: string }> = []
  readonly chat = {
    postMessage: async (input: { channel: string; text: string; thread_ts?: string; client_msg_id?: string; blocks?: readonly Record<string, unknown>[] }) => {
      this.posted.push(input)
      return { ok: true, ts: "200.001" }
    },
    update: async (input: { channel: string; ts: string; text: string; blocks?: readonly Record<string, unknown>[] }) => {
      this.updated.push(input)
      return { ok: this.updateError === undefined, error: this.updateError, ts: input.ts }
    },
  }
  readonly reactions = {
    add: async () => {
      if (this.reactionThrow !== undefined) throw this.reactionThrow
      return { ok: this.reactionError === undefined, error: this.reactionError }
    },
    remove: async () => ({ ok: true }),
    get: async () => {
      if (this.reactionReadThrow !== undefined) throw this.reactionReadThrow
      return { ok: true, message: { reactions: this.reactionPresent ? [{ name: "eyes" }] : [] } }
    },
  }
  readonly conversations = {
    history: async () => ({ ok: true, messages: this.historyMessages }),
  }
}

async function start(transport: Transport, web: Web) {
  const socket = new Socket()
  const adapter = new SlackAdapter({
    adapterId: "a",
    transport,
    socketFactory: () => socket,
    webFactory: () => web,
    heartbeatIntervalMs: 60_000,
    deliveryPollIntervalMs: 60_000,
  })
  await adapter.start(new AbortController().signal)
}

describe("Slack status provider mutations", () => {
  it("updates the durable provider message identity in place", async () => {
    const transport = new Transport()
    const web = new Web()
    transport.deliveries.push({
      id: "progress",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "chat_update",
        text: "Completed",
        providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
      }),
    })

    await start(transport, web)

    expect(web.updated).toEqual([{ channel: "C1", ts: "100.002", text: "Completed" }])
    expect(transport.acks).toEqual([{
      id: "progress",
      outcome: "delivered",
      adapterId: "a",
      providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
    }])
  })

  it("sends the Server-provided blocks with a control update", async () => {
    const transport = new Transport()
    const web = new Web()
    const blocks = [{ type: "section", text: { type: "mrkdwn", text: "Stop requested." } }]
    transport.deliveries.push({
      id: "control",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "chat_update",
        text: "Stop requested.",
        providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
        blocks,
      }),
    })

    await start(transport, web)

    expect(web.updated).toEqual([{
      channel: "C1",
      ts: "100.002",
      text: "Stop requested.",
      blocks,
    }])
  })

  it("projects unsupported source reactions to one same-thread fallback message", async () => {
    const transport = new Transport()
    const web = new Web()
    web.reactionError = "cant_react"
    transport.deliveries.push({
      id: "received",
      conversationId: "D1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "reaction_add",
        reaction: "eyes",
        targetMessageIdentity: { conversationId: "D1", messageTs: "100.001" },
        fallbackText: "Received",
        fallbackDispatchRef: "fallback:received",
      }),
    })

    await start(transport, web)

    expect(web.posted).toEqual([{
      channel: "D1",
      text: "Received",
      thread_ts: "100.001",
      client_msg_id: "fallback:received",
    }])
    expect(transport.acks[0]).toMatchObject({ id: "received", outcome: "delivered", adapterId: "a" })
  })

  it("projects a thrown missing_scope reaction to one fallback before delivering the final text", async () => {
    const transport = new Transport()
    const web = new Web()
    web.reactionThrow = new Error("An API error occurred: missing_scope")
    transport.deliveries.push({
      id: "received",
      conversationId: "D1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "reaction_add",
        reaction: "eyes",
        targetMessageIdentity: { conversationId: "D1", messageTs: "100.001" },
        fallbackText: "Received",
        fallbackDispatchRef: "fallback:received",
      }),
    }, {
      id: "terminal",
      conversationId: "D1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({ operation: "post_message", text: "Final answer" }),
    })

    await start(transport, web)

    expect(web.posted).toEqual([
      { channel: "D1", text: "Received", thread_ts: "100.001", client_msg_id: "fallback:received" },
      { channel: "D1", text: "Final answer", thread_ts: "100.001" },
    ])
    expect(transport.acks).toEqual([
      { id: "received", outcome: "delivered", adapterId: "a", providerMessageIdentity: { conversationId: "D1", messageTs: "200.001" } },
      { id: "terminal", outcome: "delivered", adapterId: "a", providerMessageIdentity: { conversationId: "D1", messageTs: "200.001" } },
    ])
  })

  it("uses one stable same-thread fallback after a terminal update failure", async () => {
    const transport = new Transport()
    const web = new Web()
    web.updateError = "message_not_found"
    transport.deliveries.push({
      id: "terminal",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "chat_update",
        text: "Completed",
        providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
        fallbackText: "Completed",
        fallbackDispatchRef: "fallback:terminal",
      }),
    })

    await start(transport, web)

    expect(web.updated).toEqual([{ channel: "C1", ts: "100.002", text: "Completed" }])
    expect(web.posted).toEqual([{
      channel: "C1",
      text: "Completed",
      thread_ts: "100.001",
      client_msg_id: "fallback:terminal",
    }])
    expect(transport.acks).toEqual([{ id: "terminal", outcome: "delivered", adapterId: "a", providerMessageIdentity: { conversationId: "C1", messageTs: "200.001" } }])
  })

  it("reconciles uncertain updates before allowing another mutation", async () => {
    const transport = new Transport()
    const web = new Web()
    web.historyMessages = [{ ts: "100.002", text: "Completed" }]
    transport.uncertain.push({
      id: "uncertain",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "chat_update",
        text: "Completed",
        providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
      }),
    })

    await start(transport, web)

    expect(web.updated).toEqual([])
    expect(transport.acks).toEqual([{
      id: "uncertain",
      outcome: "delivered",
      adapterId: "a",
      providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
    }])
  })

  it("reconciles an uncertain reaction missing_scope with one fallback and a delivered identity", async () => {
    const transport = new Transport()
    const web = new Web()
    web.reactionReadThrow = new Error("An API error occurred: missing_scope")
    transport.deliveries.push({
      id: "terminal",
      conversationId: "D1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({ operation: "post_message", text: "Final answer" }),
    })
    transport.uncertain.push({
      id: "uncertain-received",
      conversationId: "D1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "reaction_add",
        reaction: "eyes",
        targetMessageIdentity: { conversationId: "D1", messageTs: "100.001" },
        fallbackText: "Received",
        fallbackDispatchRef: "fallback:received",
      }),
    })

    await start(transport, web)

    expect(web.posted).toEqual([
      { channel: "D1", text: "Received", thread_ts: "100.001", client_msg_id: "fallback:received" },
      { channel: "D1", text: "Final answer", thread_ts: "100.001" },
    ])
    expect(transport.acks).toEqual([
      {
        id: "uncertain-received",
        outcome: "delivered",
        adapterId: "a",
        providerMessageIdentity: { conversationId: "D1", messageTs: "200.001" },
      },
      {
        id: "terminal",
        outcome: "delivered",
        adapterId: "a",
        providerMessageIdentity: { conversationId: "D1", messageTs: "200.001" },
      },
    ])
  })

  it("reconciles a delivered uncertain reaction with its target identity", async () => {
    const transport = new Transport()
    const web = new Web()
    web.reactionPresent = true
    transport.uncertain.push({
      id: "uncertain-reaction",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "reaction_add",
        reaction: "eyes",
        targetMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
      }),
    })

    await start(transport, web)

    expect(transport.acks).toEqual([{
      id: "uncertain-reaction",
      outcome: "delivered",
      adapterId: "a",
      providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
    }])
  })

  it("reconciles an explicit unknown operation without posting a message", async () => {
    const transport = new Transport()
    const web = new Web()
    web.historyMessages = [{ ts: "100.003", client_msg_id: "unknown:1" }]
    transport.deliveries.push({
      id: "unknown",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({ operation: "delete_message", clientMessageId: "unknown:1", text: "ignored" }),
    })

    await start(transport, web)

    expect(web.posted).toEqual([])
    expect(web.updated).toEqual([])
    expect(transport.acks).toContainEqual({
      id: "unknown",
      outcome: "delivered",
      adapterId: "a",
    })
  })

  it("does not persist a reaction target as a created message identity", async () => {
    const transport = new Transport()
    const web = new Web()
    transport.deliveries.push({
      id: "reaction",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "reaction_add",
        targetMessageIdentity: { conversationId: "C1", messageTs: "100.001" },
        reaction: "eyes",
      }),
    })

    await start(transport, web)

    expect(transport.acks).toContainEqual({ id: "reaction", outcome: "delivered", adapterId: "a" })
  })

  it("posts the stable fallback only after reconciliation confirms an update target is absent", async () => {
    const transport = new Transport()
    const web = new Web()
    transport.uncertain.push({
      id: "uncertain-missing",
      conversationId: "C1",
      threadTs: "100.001",
      payloadJson: JSON.stringify({
        operation: "chat_update",
        text: "Completed",
        providerMessageIdentity: { conversationId: "C1", messageTs: "100.002" },
        fallbackText: "Completed",
        fallbackDispatchRef: "fallback:terminal",
      }),
    })

    await start(transport, web)

    expect(web.updated).toEqual([])
    expect(web.posted).toEqual([{
      channel: "C1",
      text: "Completed",
      thread_ts: "100.001",
      client_msg_id: "fallback:terminal",
    }])
    expect(transport.acks).toContainEqual({
      id: "uncertain-missing",
      outcome: "delivered",
      adapterId: "a",
      providerMessageIdentity: { conversationId: "C1", messageTs: "200.001" },
    })
  })
})
