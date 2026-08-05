import type { AdapterTransport, Delivery, DeliveryAck, IngressResult, ProviderMessageIdentity, SlackAdapterTarget, SlackConnectionRef, SlackEnvelope, SlackFileRef, SlackInteractionEnvelope, SlackManagerRef, SlackSenderKind, SlackWebClient, SocketClient, SocketClientFactory, WebClientFactory } from "./types.js"

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
  readonly target: SlackAdapterTarget
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
      const runtime: ConnectionRuntime = { target: ref, draining: false }
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
    const session = await this.options.transport.lease(runtime.target, this.options.adapterId, signal)
    if (isManagerTarget(runtime.target)) {
      if (!session.botToken) throw new Error("Manager lease did not return Slack credentials")
      runtime.web = this.options.webFactory(session.botToken, runtime.target)
    } else {
      if (!session.appToken || !session.botToken) throw new Error("Connection lease did not return Slack credentials")
      runtime.web = this.options.webFactory(session.botToken, runtime.target)
      runtime.socket = this.options.socketFactory(session.appToken, runtime.target)
      runtime.socket.on("slack_event", async (event) => {
        const interaction = isSlackInteraction(event.body)
        try {
          await this.handleEvent(runtime, event.body, event.ack, signal)
        } catch (error) {
          console.error(
            interaction
              ? "Slack interaction processing failed after acknowledgement."
              : "Slack event handling failed; the event remains unacknowledged for retry.",
            safeErrorMessage(error),
          )
        }
      })
      await runtime.socket.start()
    }
    const heartbeatMs = Math.max(1_000, this.options.heartbeatIntervalMs ?? 15_000)
    const pollMs = Math.max(100, this.options.deliveryPollIntervalMs ?? 1_000)
    runtime.heartbeatTimer = setInterval(() => void this.refresh(runtime, signal), heartbeatMs)
    runtime.deliveryTimer = setInterval(() => void this.drain(runtime, signal), pollMs)
    await this.drain(runtime, signal)
  }

  private async refresh(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (this.stopped || signal.aborted) return
    try {
      const session = await this.options.transport.lease(runtime.target, this.options.adapterId, signal)
      if (isManagerTarget(runtime.target)) {
        runtime.web = session.botToken
          ? this.options.webFactory(session.botToken, runtime.target)
          : undefined
      } else if (session.botToken) {
        runtime.web = this.options.webFactory(session.botToken, runtime.target)
      }
      await this.drain(runtime, signal)
    } catch {
      // The next heartbeat retries the lease; Server state remains authoritative.
    }
  }

  private async handleEvent(runtime: ConnectionRuntime, body: unknown, ack: () => Promise<void> | void, signal: AbortSignal): Promise<void> {
    const interaction = isSlackInteraction(body)
    if (interaction) await ack()
    await this.acquire(signal)
    try {
      if (interaction) {
        await this.options.transport.interaction(runtime.target, normalizeSlackInteraction(body), signal)
        await this.drain(runtime, signal)
        return
      }
      const envelope = normalizeSocketEvent(body)
      const result = await this.options.transport.ingress(runtime.target, envelope, signal)
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
      await this.drainUncertain(runtime, signal)
      while (!signal.aborted) {
        const delivery = await this.options.transport.claimDelivery(runtime.target, this.options.adapterId, signal)
        if (!delivery) break
        try {
          const ack = await this.mutateDelivery(runtime.web, delivery)
          await this.options.transport.ackDelivery(runtime.target, withAdapterId(ack, this.options.adapterId), signal)
        } catch (error) {
          await this.options.transport.ackDelivery(runtime.target, withAdapterId({
            id: delivery.id,
            outcome: "uncertain",
            reason: error instanceof Error ? error.message : String(error),
          }, this.options.adapterId), signal)
          continue
        }
      }
    } finally {
      runtime.draining = false
    }
  }

  private async mutateDelivery(web: SlackWebClient, delivery: Delivery): Promise<DeliveryAck> {
    const payload = parseDeliveryPayload(delivery.payloadJson)
    const operation = payload.operation ?? "post_message"
    if (!isKnownDeliveryOperation(operation))
      return await this.reconcileForUnknownOperation(web, delivery)
    if (operation === "chat_update") {
      const target = payload.providerMessageIdentity
      if (!target) throw new Error("chat.update delivery has no provider message identity")
      const response = await web.chat.update?.({
        channel: target.conversationId,
        ts: target.messageTs,
        text: requiredText(payload),
        ...(payload.blocks ? { blocks: payload.blocks } : {}),
      })
      if (!response) throw new Error("Slack client does not support chat.update")
      if (response.ok === false)
        return await this.fallbackAfterUpdateFailure(web, delivery, payload, response.error ?? "Slack rejected chat.update")
      return delivered(delivery, { conversationId: target.conversationId, messageTs: response.ts ?? target.messageTs })
    }

    if (operation === "reaction_add" || operation === "reaction_remove") {
      const target = payload.targetMessageIdentity
      if (!target || !payload.reaction) throw new Error(`${operation} delivery is missing its target`)
      const response = await mutateReaction(web, operation, target, payload.reaction)
      if (!response) throw new Error("Slack client does not support reactions")
      if (response.ok === false) {
        if (!isUnsupportedReactionError(response.error))
          return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the reaction" }
        if (response.error === "missing_scope")
          return payload.fallbackText && payload.fallbackDispatchRef
            ? await this.postFallback(web, delivery, payload, response.error)
            : delivered(delivery)
        const statusTarget = payload.statusDispatchRef
          ? await findStatusMessage(web, delivery.conversationId, payload.statusDispatchRef)
          : undefined
        if (statusTarget && statusTarget.messageTs !== target.messageTs) {
          const statusResponse = await mutateReaction(web, operation, statusTarget, payload.reaction)
          if (!statusResponse) throw new Error("Slack client does not support reactions")
          if (statusResponse.ok !== false)
            return delivered(delivery)
          if (!isUnsupportedReactionError(statusResponse.error))
            return { id: delivery.id, outcome: "retry", reason: statusResponse.error ?? "Slack rejected the reaction" }
        }
        if (operation === "reaction_remove")
          return delivered(delivery)
        if (payload.fallbackText && payload.fallbackDispatchRef)
          return await this.postFallback(web, delivery, payload, response.error ?? "Slack does not support reactions")
        return delivered(delivery)
      }
      return delivered(delivery)
    }

    const text = requiredText(payload)
    const existingStatus = payload.statusDispatchRef
      ? await findStatusMessage(web, delivery.conversationId, payload.statusDispatchRef)
      : undefined
    if (existingStatus && web.chat.update) {
      const response = await web.chat.update({
        channel: existingStatus.conversationId,
        ts: existingStatus.messageTs,
        text,
        ...(payload.blocks ? { blocks: payload.blocks } : {}),
      })
      if (response.ok === false)
        return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the status update" }
      return delivered(delivery, { conversationId: existingStatus.conversationId, messageTs: response.ts ?? existingStatus.messageTs })
    }
    const response = await web.chat.postMessage({
      channel: delivery.conversationId,
      text,
      ...(delivery.threadTs ? { thread_ts: delivery.threadTs } : {}),
      ...(payload.clientMessageId ? { client_msg_id: payload.clientMessageId } : {}),
      ...(payload.blocks ? { blocks: payload.blocks } : {}),
    })
    if (response.ok === false)
      return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the post" }
    return delivered(delivery, response.ts ? { conversationId: delivery.conversationId, messageTs: response.ts } : undefined)
  }

  private async fallbackAfterUpdateFailure(web: SlackWebClient, delivery: Delivery, payload: DeliveryPayload, reason: string) {
    if (!payload.fallbackText || !payload.fallbackDispatchRef)
      return { id: delivery.id, outcome: "retry" as const, reason }
    return await this.postFallback(web, delivery, payload, reason)
  }

  private async postFallback(web: SlackWebClient, delivery: Delivery, payload: DeliveryPayload, reason: string): Promise<DeliveryAck> {
    const response = await web.chat.postMessage({
      channel: delivery.conversationId,
      text: payload.fallbackText ?? requiredText(payload),
      ...(delivery.threadTs ? { thread_ts: delivery.threadTs } : {}),
      client_msg_id: payload.fallbackDispatchRef,
      ...(payload.blocks ? { blocks: payload.blocks } : {}),
    })
    if (response.ok === false)
      return { id: delivery.id, outcome: "retry", reason: response.error ?? reason }
    return delivered(delivery, response.ts ? { conversationId: delivery.conversationId, messageTs: response.ts } : undefined)
  }

  private async reconcile(web: SlackWebClient | undefined, delivery: Delivery): Promise<DeliveryAck> {
    const payload = parseDeliveryPayload(delivery.payloadJson)
    const target = payload.providerMessageIdentity ?? payload.targetMessageIdentity
    if ((payload.operation === "reaction_add" || payload.operation === "reaction_remove") && target && payload.reaction) {
      const response = await getReaction(web, target)
      if (!web || !response) return { id: delivery.id, outcome: "uncertain", reason: "Slack client cannot reconcile reactions" }
      if (response.ok === false) {
        if (!isUnsupportedReactionError(response.error))
          return { id: delivery.id, outcome: "uncertain", reason: response.error ?? "Slack reaction reconciliation failed" }
        return payload.fallbackText && payload.fallbackDispatchRef
          ? await this.postFallback(web, delivery, payload, response.error ?? "Slack reaction reconciliation failed")
          : delivered(delivery, target)
      }
      const present = response.message?.reactions?.some(reaction => reaction.name === payload.reaction)
      const deliveredState = payload.operation === "reaction_add" ? present : !present
      return deliveredState ? delivered(delivery, target) : { id: delivery.id, outcome: "retry", reason: "provider_mutation_absent" }
    }

    const history = await web?.conversations?.history?.({
      channel: delivery.conversationId,
      ...(target ? { latest: target.messageTs, oldest: target.messageTs } : {}),
      inclusive: true,
      limit: 200,
    })
    if (!history) return { id: delivery.id, outcome: "uncertain", reason: "Slack client cannot reconcile messages" }
    if (history.ok === false) return { id: delivery.id, outcome: "uncertain", reason: history.error ?? "Slack message reconciliation failed" }
    const message = history.messages?.find(candidate =>
      (target && candidate.ts === target.messageTs)
      || (payload.clientMessageId && candidate.client_msg_id === payload.clientMessageId)
      || (payload.fallbackDispatchRef && candidate.client_msg_id === payload.fallbackDispatchRef))
    if (message?.ts) {
      if (payload.operation === "chat_update" && payload.text && message.text !== payload.text)
        return { id: delivery.id, outcome: "retry", reason: "provider_mutation_absent" }
      return isKnownDeliveryOperation(payload.operation ?? "post_message")
        ? delivered(delivery, { conversationId: delivery.conversationId, messageTs: message.ts })
        : delivered(delivery)
    }
    if (payload.operation === "chat_update" && payload.fallbackText && payload.fallbackDispatchRef)
      return web
        ? await this.postFallback(web, delivery, payload, "provider_mutation_absent")
        : { id: delivery.id, outcome: "uncertain", reason: "Slack client cannot post fallback" }
    return { id: delivery.id, outcome: "retry", reason: "provider_mutation_absent" }
  }

  private async reconcileForUnknownOperation(web: SlackWebClient, delivery: Delivery): Promise<DeliveryAck> {
    return await this.reconcile(web, delivery)
  }

  private async drainUncertain(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (!this.options.transport.claimUncertainDelivery) return
    while (!signal.aborted) {
      const delivery = await this.options.transport.claimUncertainDelivery(runtime.target, this.options.adapterId, signal)
      if (!delivery) return
      try {
        const ack = await this.reconcile(runtime.web, delivery)
        await this.options.transport.ackDelivery(runtime.target, withAdapterId(ack, this.options.adapterId), signal)
        if (ack.outcome === "uncertain") return
      } catch (error) {
        await this.options.transport.ackDelivery(runtime.target, withAdapterId({
          id: delivery.id,
          outcome: "uncertain",
          reason: error instanceof Error ? error.message : String(error),
        }, this.options.adapterId), signal)
        return
      }
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

function connectionKey(ref: SlackAdapterTarget) {
  return isManagerTarget(ref)
    ? `manager:${ref.enrollmentId}`
    : `connection:${ref.projectId}:${ref.connectionId}`
}

function safeErrorMessage(error: unknown) {
  const message = error instanceof Error ? error.message : String(error)
  return message.replace(/(?:xapp|xoxb|xoxp|xoxe)[.A-Za-z0-9_-]*/gi, "<redacted>")
}

function isManagerTarget(value: SlackAdapterTarget): value is SlackManagerRef {
  return value.ownerKind === "manager"
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

export function normalizeSlackInteraction(body: unknown): SlackInteractionEnvelope {
  const payload = interactionPayload(body)
  if (!payload || payload.type !== "block_actions") throw new Error("Slack interaction is malformed")
  const team = isRecord(payload.team) ? stringValue(payload.team.id) : stringValue(payload, "team_id")
  const user = isRecord(payload.user) ? stringValue(payload.user.id) : null
  const container = isRecord(payload.container) ? payload.container : undefined
  const conversationId = stringValue(container?.channel_id)
  const messageTs = stringValue(container?.message_ts)
  const actions = Array.isArray(payload.actions) ? payload.actions : []
  const action = actions.length > 0 && isRecord(actions[0]) ? actions[0] : undefined
  const interactionId = stringValue(payload.trigger_id) ?? stringValue(action?.action_ts) ?? stringValue(payload, "event_id")
  const actionId = stringValue(action?.action_id)
  const actionValue = stringValue(action?.value)
  if (!team || !user || !conversationId || !messageTs || !interactionId || !actionId || !actionValue)
    throw new Error("Slack interaction is missing its stable identity")
  return {
    eventType: "block_actions",
    interactionId,
    teamId: team,
    conversationId,
    messageTs,
    threadTs: stringValue(container?.thread_ts),
    actorSlackUserId: user,
    actionId,
    actionValue,
  }
}

function isSlackInteraction(value: unknown): value is Record<string, unknown> {
  return interactionPayload(value)?.type === "block_actions"
}

function interactionPayload(value: unknown): Record<string, unknown> | null {
  if (!isRecord(value)) return null
  if (value.type === "block_actions") return value
  if (value.type !== "interactive") return null
  const payload = value.payload
  if (isRecord(payload)) return payload
  if (typeof payload !== "string") return null
  try {
    const parsed: unknown = JSON.parse(payload)
    return isRecord(parsed) ? parsed : null
  } catch {
    return null
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

interface DeliveryPayload {
  readonly operation?: string
  readonly text?: string
  readonly clientMessageId?: string
  readonly providerMessageIdentity?: ProviderMessageIdentity
  readonly targetMessageIdentity?: ProviderMessageIdentity
  readonly reaction?: string
  readonly fallbackText?: string
  readonly fallbackDispatchRef?: string
  readonly statusDispatchRef?: string
  readonly blocks?: readonly Record<string, unknown>[]
}

function parseDeliveryPayload(value: string): DeliveryPayload {
  const parsed: unknown = JSON.parse(value)
  if (!isRecord(parsed)) throw new Error("Delivery payload was not an object")
  return parsed as DeliveryPayload
}

function requiredText(payload: DeliveryPayload): string {
  if (!payload.text) throw new Error("Delivery payload did not contain text")
  return payload.text
}

function delivered(delivery: Delivery, identity?: ProviderMessageIdentity): DeliveryAck {
  return identity
    ? { id: delivery.id, outcome: "delivered", providerMessageIdentity: identity }
    : { id: delivery.id, outcome: "delivered" }
}

function withAdapterId(ack: DeliveryAck, adapterId: string): DeliveryAck {
  return { ...ack, adapterId }
}

function isKnownDeliveryOperation(operation: unknown): operation is "post_message" | "chat_update" | "reaction_add" | "reaction_remove" {
  return operation === "post_message"
    || operation === "chat_update"
    || operation === "reaction_add"
    || operation === "reaction_remove"
}

function isUnsupportedReactionError(error: string | undefined): boolean {
  return new Set([
    "cant_react",
    "message_not_found",
    "not_in_channel",
    "not_allowed_token_type",
    "invalid_timestamp",
    "channel_not_found",
    "missing_scope",
  ]).has(error ?? "")
}

async function mutateReaction(
  web: SlackWebClient,
  operation: "reaction_add" | "reaction_remove",
  target: ProviderMessageIdentity,
  reaction: string,
) {
  const method = operation === "reaction_add" ? web.reactions?.add : web.reactions?.remove
  try {
    return await method?.call(web.reactions, {
      channel: target.conversationId,
      name: reaction,
      timestamp: target.messageTs,
    })
  } catch (error) {
    const code = slackErrorCode(error)
    if (!code) throw error
    return { ok: false, error: code }
  }
}

async function getReaction(web: SlackWebClient | undefined, target: ProviderMessageIdentity) {
  try {
    return await web?.reactions?.get?.({
      channel: target.conversationId,
      timestamp: target.messageTs,
      full: true,
    })
  } catch (error) {
    const code = slackErrorCode(error)
    if (!code) throw error
    return { ok: false, error: code }
  }
}

function slackErrorCode(error: unknown): string | undefined {
  if (isRecord(error)) {
    const data = isRecord(error.data) ? error.data : undefined
    const dataError = stringValue(data?.error)
    if (dataError) return dataError
    const directError = stringValue(error.error)
    if (directError) return directError
    const message = stringValue(error.message)
    if (message) return slackErrorCodeFromMessage(message)
  }
  return error instanceof Error ? slackErrorCodeFromMessage(error.message) : undefined
}

function slackErrorCodeFromMessage(message: string): string | undefined {
  return message.match(/API error occurred:\s*([a-z][a-z0-9_]*)/i)?.[1]
}

async function findStatusMessage(
  web: SlackWebClient,
  conversationId: string,
  clientMessageId: string,
): Promise<ProviderMessageIdentity | undefined> {
  const history = await web.conversations?.history?.({ channel: conversationId, limit: 200 })
  if (!history || history.ok === false) return undefined
  const message = history.messages?.find(candidate => candidate.client_msg_id === clientMessageId && candidate.ts)
  return message?.ts ? { conversationId, messageTs: message.ts } : undefined
}

function stringValue(value: unknown, key?: string): string | null {
  const candidate = key && isRecord(value) ? value[key] : value
  return typeof candidate === "string" && candidate.length > 0 ? candidate : null
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}
