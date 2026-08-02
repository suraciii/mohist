import type { AdapterTransport, IngressResult, SlackConnectionRef, SlackEnvelope, SlackFileRef, SlackSenderKind, SlackWebClient, SocketClient, SocketClientFactory, WebClientFactory } from "./types.js"

export interface SlackAdapterOptions {
  readonly adapterId: string
  readonly transport: AdapterTransport
  readonly socketFactory: SocketClientFactory
  readonly webFactory: WebClientFactory
  readonly discoveryIntervalMs?: number
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
  private readonly runtimes = new Map<string, ConnectionRuntime>()
  private readonly maxInFlight: number
  private discoveryTimer?: ReturnType<typeof setInterval>
  private inFlight = 0
  private stopped = false

  constructor(private readonly options: SlackAdapterOptions) {
    this.maxInFlight = Math.max(1, Math.floor(options.maxInFlight ?? 8))
  }

  async start(signal: AbortSignal): Promise<void> {
    signal.addEventListener("abort", () => this.stop(), { once: true })
    await this.refreshConnections(signal)
    const discoveryMs = Math.max(1_000, this.options.discoveryIntervalMs ?? 15_000)
    this.discoveryTimer = setInterval(() => void this.refreshConnections(signal).catch(() => undefined), discoveryMs)
  }

  stop() {
    if (this.stopped) return
    this.stopped = true
    if (this.discoveryTimer) clearInterval(this.discoveryTimer)
    for (const runtime of this.runtimes.values()) this.disconnect(runtime)
    this.runtimes.clear()
  }

  async refreshConnections(signal: AbortSignal): Promise<void> {
    if (this.stopped || signal.aborted) return
    const connections = await this.options.transport.discoverConnections(signal)
    const currentKeys = new Set(connections.map(connectionKey))
    for (const [key, runtime] of this.runtimes) {
      if (!currentKeys.has(key)) {
        this.disconnect(runtime)
        this.runtimes.delete(key)
      }
    }
    await Promise.all(connections.map(async (ref) => {
      const key = connectionKey(ref)
      if (this.runtimes.has(key)) return
      const runtime: ConnectionRuntime = { ref, draining: false }
      this.runtimes.set(key, runtime)
      try {
        await this.connect(runtime, signal)
      } catch {
        this.runtimes.delete(key)
        this.disconnect(runtime)
      }
    }))
  }

  private disconnect(runtime: ConnectionRuntime) {
    if (runtime.heartbeatTimer) clearInterval(runtime.heartbeatTimer)
    if (runtime.deliveryTimer) clearInterval(runtime.deliveryTimer)
    void runtime.socket?.disconnect?.()
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
      await this.renderUserFacingRejection(runtime, envelope, result, signal)
      await ack()
      await this.drain(runtime, signal)
    } finally {
      this.inFlight -= 1
    }
  }

  private async renderUserFacingRejection(
    runtime: ConnectionRuntime,
    envelope: SlackEnvelope,
    result: IngressResult,
    signal: AbortSignal,
  ): Promise<void> {
    if (result.kind !== "backpressured") return
    if (runtime.draining || this.stopped || !runtime.web) return
    const reason = result.reason ?? "This Slack Connection is backpressured; retry after pending deliveries drain."
    const message: { channel: string; text: string; thread_ts?: string } = {
      channel: envelope.conversationId,
      text: reason,
    }
    if (envelope.threadTs) message.thread_ts = envelope.threadTs
    await runtime.web.chat.postMessage(message)
  }

  private async drain(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (runtime.draining || this.stopped || !runtime.web) return
    runtime.draining = true
    try {
      while (!signal.aborted) {
        const delivery = await this.options.transport.claimDelivery(runtime.ref, this.options.adapterId, signal)
        if (!delivery) return
        let response: { ok?: boolean; error?: string } | undefined
        try {
          const payload = JSON.parse(delivery.payloadJson) as unknown
          const text = readText(payload)
          if (!text) throw new Error("Delivery payload did not contain text")
          const message: { channel: string; text: string; thread_ts?: string } = {
            channel: delivery.conversationId,
            text,
          }
          if (delivery.threadTs) message.thread_ts = delivery.threadTs
          response = await runtime.web.chat.postMessage(message)
        } catch (error) {
          await this.options.transport.ackDelivery(runtime.ref, { id: delivery.id, outcome: "uncertain", reason: error instanceof Error ? error.message : String(error) }, signal)
          continue
        }
        if (response.ok === false) {
          await this.options.transport.ackDelivery(runtime.ref, { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the post" }, signal)
          continue
        }
        await this.options.transport.ackDelivery(runtime.ref, { id: delivery.id, outcome: "delivered" }, signal)
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

function connectionKey(ref: SlackConnectionRef) {
  return `${ref.projectId}:${ref.connectionId}`
}

export function normalizeSocketEvent(body: unknown): SlackEnvelope {
  const event = isRecord(body) && isRecord(body.event) ? body.event : body
  if (!isRecord(event)) throw new Error("Slack Socket Mode event is malformed")
  const teamId = stringValue(event.team_id) ?? stringValue(body, "team_id")
  const conversationId = stringValue(event.channel)
  const messageTs = stringValue(event.ts) ?? stringValue(event.event_ts)
  if (!teamId || !conversationId || !messageTs) throw new Error("Slack event is missing its stable identity")
  const senderSlackUserId = stringValue(event.user)
  return {
    eventType: stringValue(event.type) ?? "message",
    isDirectMessage: event.channel_type === "im" || conversationId.startsWith("D"),
    teamId,
    conversationId,
    messageTs,
    threadTs: stringValue(event.thread_ts),
    mentionedUserIds: parseMentionedUserIds(typeof event.text === "string" ? event.text : null),
    senderSlackUserId,
    senderKind: normalizeSenderKind(event),
    text: typeof event.text === "string" ? event.text : null,
    files: parseFiles(event.files),
  }
}

function parseFiles(value: unknown): readonly SlackFileRef[] {
  if (!Array.isArray(value)) return []
  return value.flatMap((candidate) => {
    if (!isRecord(candidate)) return []
    const id = stringValue(candidate.id)
    const name = stringValue(candidate.name)
    const mimetype = stringValue(candidate.mimetype)
    const size = candidate.size
    return id && name && mimetype && typeof size === "number" && Number.isSafeInteger(size) && size >= 0
      ? [{ id, name, mimetype, size }]
      : []
  })
}

function normalizeSenderKind(event: Record<string, unknown>): SlackSenderKind {
  if (stringValue(event.bot_id) || stringValue(event.subtype) === "bot_message")
    return "bot"
  return stringValue(event.user) ? "human" : "unknown"
}

function parseMentionedUserIds(text: string | null): readonly string[] {
  if (!text) return []
  const mentioned = new Set<string>()
  const pattern = /<@([A-Za-z0-9_-]+)(?:\|[^>]*)?>/g
  for (const match of text.matchAll(pattern)) {
    const userId = match[1]
    if (userId) mentioned.add(userId)
  }
  return [...mentioned]
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
