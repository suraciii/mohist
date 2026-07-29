import type { AdapterTransport, SlackConnectionRef, SlackEnvelope, SlackWebClient, SocketClient, SocketClientFactory, WebClientFactory } from "./types.js"

export interface SlackAdapterOptions {
  readonly adapterId: string
  readonly connections: readonly SlackConnectionRef[]
  readonly transport: AdapterTransport
  readonly socketFactory: SocketClientFactory
  readonly webFactory: WebClientFactory
  readonly heartbeatIntervalMs?: number
  readonly deliveryPollIntervalMs?: number
  readonly maxInFlight?: number
}

interface ConnectionRuntime {
  readonly ref: SlackConnectionRef
  socket?: SocketClient
  web?: SlackWebClient
  heartbeatTimer?: ReturnType<typeof setInterval>
  deliveryTimer?: ReturnType<typeof setInterval>
  draining: boolean
}

export class SlackAdapter {
  private readonly runtimes: ConnectionRuntime[]
  private readonly maxInFlight: number
  private inFlight = 0
  private stopped = false

  constructor(private readonly options: SlackAdapterOptions) {
    if (options.connections.length === 0) throw new Error("At least one Slack Connection is required")
    this.maxInFlight = Math.max(1, Math.floor(options.maxInFlight ?? 8))
    this.runtimes = options.connections.map((ref) => ({ ref, draining: false }))
  }

  async start(signal: AbortSignal): Promise<void> {
    signal.addEventListener("abort", () => this.stop(), { once: true })
    await Promise.all(this.runtimes.map((runtime) => this.connect(runtime, signal)))
  }

  stop() {
    if (this.stopped) return
    this.stopped = true
    for (const runtime of this.runtimes) {
      if (runtime.heartbeatTimer) clearInterval(runtime.heartbeatTimer)
      if (runtime.deliveryTimer) clearInterval(runtime.deliveryTimer)
      void runtime.socket?.disconnect?.()
    }
  }

  private async connect(runtime: ConnectionRuntime, signal: AbortSignal) {
    const session = await this.options.transport.lease(runtime.ref, this.options.adapterId, signal)
    runtime.web = this.options.webFactory(session.botToken, runtime.ref)
    runtime.socket = this.options.socketFactory(session.appToken, runtime.ref)
    runtime.socket.on("slack_event", async (event) => this.handleEvent(runtime, event.body, event.ack, signal))
    await runtime.socket.start()
    const heartbeatMs = Math.max(1_000, this.options.heartbeatIntervalMs ?? 15_000)
    const pollMs = Math.max(100, this.options.deliveryPollIntervalMs ?? 1_000)
    runtime.heartbeatTimer = setInterval(() => void this.refresh(runtime, signal), heartbeatMs)
    runtime.deliveryTimer = setInterval(() => void this.drain(runtime, signal), pollMs)
    await this.drain(runtime, signal)
  }

  private async refresh(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (this.stopped || signal.aborted) return
    try {
      const session = await this.options.transport.lease(runtime.ref, this.options.adapterId, signal)
      runtime.web = this.options.webFactory(session.botToken, runtime.ref)
      await this.drain(runtime, signal)
    } catch {
      // The next heartbeat retries the lease; Server state remains authoritative.
    }
  }

  private async handleEvent(runtime: ConnectionRuntime, body: unknown, ack: () => Promise<void> | void, signal: AbortSignal): Promise<void> {
    await this.acquire(signal)
    try {
      const envelope = normalizeSocketEvent(body)
      const result = await this.options.transport.ingress(runtime.ref, envelope, signal)
      await ack()
      await this.drain(runtime, signal)
      void result
    } finally {
      this.inFlight -= 1
    }
  }

  private async drain(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (runtime.draining || this.stopped || !runtime.web) return
    runtime.draining = true
    try {
      while (!signal.aborted) {
        const delivery = await this.options.transport.claimDelivery(runtime.ref, this.options.adapterId, signal)
        if (!delivery) return
        try {
          const payload = JSON.parse(delivery.payloadJson) as unknown
          const text = readText(payload)
          if (!text) throw new Error("Delivery payload did not contain text")
          const response = await runtime.web.chat.postMessage({ channel: delivery.dmConversationId, text })
          if (response.ok === false) throw new Error(response.error ?? "Slack chat.postMessage failed")
          await this.options.transport.ackDelivery(runtime.ref, { id: delivery.id, outcome: "delivered" }, signal)
        } catch (error) {
          await this.options.transport.ackDelivery(runtime.ref, { id: delivery.id, outcome: "uncertain", reason: error instanceof Error ? error.message : String(error) }, signal)
        }
      }
    } finally {
      runtime.draining = false
    }
  }

  private async acquire(signal: AbortSignal) {
    while (this.inFlight >= this.maxInFlight) {
      if (signal.aborted) throw signal.reason
      await new Promise<void>((resolve) => setTimeout(resolve, 5))
    }
    this.inFlight += 1
  }
}

export function normalizeSocketEvent(body: unknown): SlackEnvelope {
  const event = isRecord(body) && isRecord(body.event) ? body.event : body
  if (!isRecord(event)) throw new Error("Slack Socket Mode event is malformed")
  const teamId = stringValue(event.team_id) ?? stringValue(body, "team_id")
  const conversationId = stringValue(event.channel)
  const messageTs = stringValue(event.ts) ?? stringValue(event.event_ts)
  const senderSlackUserId = stringValue(event.user) ?? ""
  if (!teamId || !conversationId || !messageTs || !senderSlackUserId) throw new Error("Slack event is missing its stable identity")
  return {
    eventType: stringValue(event.type) ?? "message",
    isDirectMessage: event.channel_type === "im" || conversationId.startsWith("D"),
    teamId,
    conversationId,
    messageTs,
    senderSlackUserId,
    text: typeof event.text === "string" ? event.text : null,
  }
}

function readText(value: unknown): string | null {
  return isRecord(value) && typeof value.text === "string" ? value.text : null
}

function stringValue(value: unknown, key?: string): string | null {
  const candidate = key && isRecord(value) ? value[key] : value
  return typeof candidate === "string" && candidate.length > 0 ? candidate : null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}
