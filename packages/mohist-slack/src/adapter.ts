import type { AdapterLease, AdapterTransport, Delivery, DeliveryAck, IngressResult, ProviderMessageIdentity, RuntimeLease, SlackAdapterTarget, SlackEnvelope, SlackFileRef, SlackFileUploadResponse, SlackInteractionEnvelope, SlackManagerRef, SlackSenderKind, SlackWebClient, SocketClient, SocketClientFactory, WebClientFactory } from "./types.js"
import { LeaseStaleError } from "./transport.js"
import { slackLogger } from "./logger.js"

export interface SlackAdapterOptions {
  readonly adapterId: string
  readonly transport: AdapterTransport
  readonly socketFactory: SocketClientFactory
  readonly webFactory: WebClientFactory
  readonly discoveryIntervalMs?: number
  readonly heartbeatIntervalMs?: number
  readonly deliveryPollIntervalMs?: number
  readonly maxInFlight?: number
  readonly dispose?: () => Promise<void>
}

interface ConnectionRuntime {
  readonly target: SlackAdapterTarget
  generation: number
  lease: RuntimeLease
  socket?: SocketClient
  web?: SlackWebClient
  heartbeatTimer?: ReturnType<typeof setInterval>
  deliveryTimer?: ReturnType<typeof setInterval>
  draining: boolean
  drainRequested: boolean
}

interface RuntimeSnapshot {
  readonly generation: number
  readonly lease: RuntimeLease
  readonly socket: SocketClient
  readonly web: SlackWebClient
}

class StaleRuntimeError extends Error {
}

export class SlackAdapter {
  private readonly log = slackLogger.child("adapter")
  private readonly runtimes = new Map<string, ConnectionRuntime>()
  private readonly maxInFlight: number
  private discoveryTimer?: ReturnType<typeof setInterval>
  private inFlight = 0
  private stopped = false
  private stopPromise?: Promise<void>

  constructor(private readonly options: SlackAdapterOptions) {
    this.maxInFlight = Math.max(1, Math.floor(options.maxInFlight ?? 8))
  }

  async start(signal: AbortSignal): Promise<void> {
    signal.addEventListener("abort", () => void this.stop(), { once: true })
    await this.refreshConnections(signal)
    const discoveryMs = Math.max(1_000, this.options.discoveryIntervalMs ?? 15_000)
    this.discoveryTimer = setInterval(() => void this.refreshConnections(signal).catch((error) => {
      this.log.error("target discovery failed", { reason: safeErrorMessage(error) })
    }), discoveryMs)
  }

  async stop(): Promise<void> {
    if (this.stopPromise) return await this.stopPromise
    if (this.stopped) return
    this.stopped = true
    if (this.discoveryTimer) clearInterval(this.discoveryTimer)
    const pending = [...this.runtimes.values()].map((runtime) => this.disconnect(runtime))
    this.runtimes.clear()
    if (this.options.dispose) pending.push(this.options.dispose())
    this.stopPromise = Promise.allSettled(pending).then(() => undefined)
    await this.stopPromise
  }

  async refreshConnections(signal: AbortSignal): Promise<void> {
    if (this.stopped || signal.aborted) return
    const targets = await this.options.transport.discover(signal)
    const currentKeys = new Set(targets.map(connectionKey))
    for (const [key, runtime] of this.runtimes) {
      if (!currentKeys.has(key)) {
        this.runtimes.delete(key)
        void this.disconnect(runtime)
      }
    }
    await Promise.all(targets.map(async (ref) => {
      const key = connectionKey(ref)
      if (this.runtimes.has(key)) return
      try {
        const validationLease = await this.options.transport.acquireLease(ref, "validation", this.options.adapterId, signal)
        if (validationLease && validationLease.kind === "validation") await this.validate(ref, validationLease, signal)
        const lease = await this.options.transport.acquireLease(ref, "runtime", this.options.adapterId, signal)
        if (!lease || lease.kind !== "runtime") return
        const runtime: ConnectionRuntime = { target: ref, generation: 0, lease, draining: false, drainRequested: false }
        this.runtimes.set(key, runtime)
        try {
          await this.startRuntime(runtime, signal)
        } catch (error) {
          await this.removeRuntime(runtime)
          throw error
        }
      } catch (error) {
        this.log.error("target connection failed", { target: key, reason: safeErrorMessage(error) })
        this.runtimes.delete(key)
      }
    }))
  }

  private async disconnect(runtime: ConnectionRuntime): Promise<void> {
    if (runtime.heartbeatTimer) clearInterval(runtime.heartbeatTimer)
    if (runtime.deliveryTimer) clearInterval(runtime.deliveryTimer)
    await this.disconnectSocket(runtime.socket, runtime.target)
  }

  private async disconnectSocket(socket: SocketClient | undefined, target: SlackAdapterTarget): Promise<void> {
    try {
      await socket?.disconnect?.()
    } catch (error) {
      this.log.error("socket disconnect failed", {
        target: connectionKey(target),
        reason: safeErrorMessage(error),
      })
    }
  }

  private async validate(target: SlackAdapterTarget, lease: Extract<AdapterLease, { kind: "validation" }>, signal: AbortSignal) {
    const socket = this.options.socketFactory(lease.appToken, target)
    try {
      this.observeSocket(socket, target)
      const hello = await socket.start()
      await this.options.transport.reportHello(target, lease.leaseId, hello.appId, signal)
    } finally {
      await socket.disconnect?.()
    }
  }

  private async startRuntime(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (!await this.openRuntimeSocket(runtime, signal, runtime.generation)) return
    if (!this.isActive(runtime, signal)) {
      await this.disconnect(runtime)
      return
    }
    const heartbeatMs = Math.max(1_000, this.options.heartbeatIntervalMs ?? 15_000)
    const pollMs = Math.max(100, this.options.deliveryPollIntervalMs ?? 1_000)
    runtime.heartbeatTimer = setInterval(() => void this.refresh(runtime, signal), heartbeatMs)
    runtime.deliveryTimer = setInterval(() => void this.drain(runtime, signal), pollMs)
    await this.drain(runtime, signal)
  }

  private async openRuntimeSocket(runtime: ConnectionRuntime, signal: AbortSignal, generation: number): Promise<boolean> {
    const web = this.options.webFactory(runtime.lease.botToken, runtime.target)
    const socket = this.options.socketFactory(runtime.lease.appToken, runtime.target)
    this.observeSocket(socket, runtime.target)
    socket.on("slack_event", async (event) => {
      const interaction = isSlackInteraction(event.body)
      const eventType = slackEventType(event.body)
      const target = connectionKey(runtime.target)
      if (this.stopped || signal.aborted || this.runtimes.get(target) !== runtime || runtime.socket !== socket) return
      this.log.info("envelope received", { target, event: eventType })
      try {
        await this.handleEvent(runtime, event.body, event.ack, eventType, signal)
      } catch (error) {
        this.log.error(
          interaction
            ? "interaction processing failed after acknowledgement"
            : "event handling failed before acknowledgement",
          { target, event: eventType, reason: safeErrorMessage(error) },
        )
      }
    })
    try {
      await socket.start()
    } catch (error) {
      if (this.isGenerationCurrent(runtime, generation, signal)) throw error
      await this.disconnectSocket(socket, runtime.target)
      return false
    }
    if (!this.isGenerationCurrent(runtime, generation, signal)) {
      await this.disconnectSocket(socket, runtime.target)
      return false
    }
    runtime.web = web
    runtime.socket = socket
    return true
  }

  private async refresh(runtime: ConnectionRuntime, signal: AbortSignal) {
    if (!this.isActive(runtime, signal)) return
    const generation = runtime.generation
    const currentLease = runtime.lease
    try {
      const renewal = await this.options.transport.renewLease(runtime.target, currentLease.leaseId, this.options.adapterId, signal)
      if (!this.isActive(runtime, signal) || runtime.generation !== generation) return
      if (!renewal || renewal.kind !== "runtime" || renewal.leaseId !== currentLease.leaseId) {
        await this.removeRuntime(runtime)
        return
      }
      runtime.lease = { ...currentLease, generation: renewal.generation, expiresAt: renewal.expiresAt }
      await this.drain(runtime, signal)
    } catch (error) {
      if (this.stopped || signal.aborted) return
      this.log.error("target lease refresh failed", {
        target: connectionKey(runtime.target),
        reason: safeErrorMessage(error),
      })
      await this.removeRuntime(runtime)
    }
  }

  private async removeRuntime(runtime: ConnectionRuntime): Promise<void> {
    // Only the runtime the map still points at may be evicted: a stale error
    // surfacing from a superseded runtime must never delete its replacement.
    // The old runtime itself is always disconnected either way.
    if (this.runtimes.get(connectionKey(runtime.target)) === runtime)
      this.runtimes.delete(connectionKey(runtime.target))
    await this.disconnect(runtime)
  }

  private isActive(runtime: ConnectionRuntime, signal: AbortSignal): boolean {
    return !this.stopped && !signal.aborted && this.runtimes.get(connectionKey(runtime.target)) === runtime
  }

  private isGenerationCurrent(runtime: ConnectionRuntime, generation: number, signal: AbortSignal): boolean {
    return this.isActive(runtime, signal) && runtime.generation === generation
  }

  private snapshot(runtime: ConnectionRuntime, signal: AbortSignal): RuntimeSnapshot | undefined {
    if (!this.isActive(runtime, signal) || !runtime.socket || !runtime.web) return
    return { generation: runtime.generation, lease: runtime.lease, socket: runtime.socket, web: runtime.web }
  }

  private isCurrent(runtime: ConnectionRuntime, snapshot: RuntimeSnapshot, signal: AbortSignal): boolean {
    return this.isGenerationCurrent(runtime, snapshot.generation, signal)
      && runtime.socket === snapshot.socket
      && runtime.web === snapshot.web
  }

  private assertCurrent(runtime: ConnectionRuntime, snapshot: RuntimeSnapshot, signal: AbortSignal): void {
    if (!this.isCurrent(runtime, snapshot, signal)) throw new StaleRuntimeError()
  }

  private observeSocket(socket: SocketClient, target: SlackAdapterTarget) {
    const key = connectionKey(target)
    for (const state of ["connecting", "connected", "reconnecting", "disconnected"] as const) {
      socket.onState?.(state, () => this.log.info("socket state changed", { target: key, state }))
    }
    socket.onState?.("error", (error) => {
      this.log.error("socket failed", { target: key, reason: safeErrorMessage(error) })
    })
  }

  private async handleEvent(
    runtime: ConnectionRuntime,
    body: unknown,
    ack: () => Promise<void> | void,
    eventType: string,
    signal: AbortSignal,
  ): Promise<void> {
    const snapshot = this.snapshot(runtime, signal)
    if (!snapshot) return
    const interaction = isSlackInteraction(body)
    let acquired = false
    try {
      if (interaction) {
        this.assertCurrent(runtime, snapshot, signal)
        await ack()
        this.assertCurrent(runtime, snapshot, signal)
      }
      await this.acquire(signal)
      acquired = true
      this.assertCurrent(runtime, snapshot, signal)
      const target = connectionKey(runtime.target)
      this.log.info("envelope forwarding", { target, event: eventType })
      if (interaction) {
        this.assertCurrent(runtime, snapshot, signal)
        await this.options.transport.interaction(runtime.target, normalizeSlackInteraction(body), runtime.lease.leaseId, this.options.adapterId, signal)
        this.assertCurrent(runtime, snapshot, signal)
        this.log.info("interaction forwarded", { target, event: eventType })
        await this.drain(runtime, signal)
        return
      }
      const envelope = normalizeSocketEvent(body)
      this.assertCurrent(runtime, snapshot, signal)
      const result = await this.options.transport.ingress(runtime.target, envelope, runtime.lease.leaseId, this.options.adapterId, signal)
      this.assertCurrent(runtime, snapshot, signal)
      this.log.info("ingress accepted", { target, event: eventType, kind: result.kind })
      if (!await this.renderUserFacingRejection(runtime, snapshot, envelope, result, signal)) return
      this.assertCurrent(runtime, snapshot, signal)
      await ack()
      this.assertCurrent(runtime, snapshot, signal)
      await this.drain(runtime, signal)
    } catch (error) {
      if (error instanceof StaleRuntimeError) return
      if (error instanceof LeaseStaleError) {
        await this.removeRuntime(runtime)
        return
      }
      throw error
    } finally {
      if (acquired) this.inFlight -= 1
    }
  }

  private async renderUserFacingRejection(
    runtime: ConnectionRuntime,
    snapshot: RuntimeSnapshot,
    envelope: SlackEnvelope,
    result: IngressResult,
    signal: AbortSignal,
  ): Promise<boolean> {
    if (result.kind !== "backpressured") return this.isCurrent(runtime, snapshot, signal)
    if (runtime.draining) return this.isCurrent(runtime, snapshot, signal)
    if (!this.isCurrent(runtime, snapshot, signal)) return false
    const reason = result.reason ?? "This Slack Connection is backpressured; retry after pending deliveries drain."
    const message: { channel: string; text: string; thread_ts?: string } = {
      channel: envelope.conversationId,
      text: reason,
    }
    if (envelope.threadTs) message.thread_ts = envelope.threadTs
    this.assertCurrent(runtime, snapshot, signal)
    await snapshot.web.chat.postMessage(message)
    return this.isCurrent(runtime, snapshot, signal)
  }

  private async drain(runtime: ConnectionRuntime, signal: AbortSignal) {
    const snapshot = this.snapshot(runtime, signal)
    if (!snapshot) return
    if (runtime.draining) {
      runtime.drainRequested = true
      return
    }
    runtime.draining = true
    try {
      await this.drainUncertain(runtime, snapshot, signal)
      this.assertCurrent(runtime, snapshot, signal)
      while (!signal.aborted) {
        this.assertCurrent(runtime, snapshot, signal)
        const delivery = await this.options.transport.claimDelivery(runtime.target, runtime.lease.leaseId, this.options.adapterId, signal)
        this.assertCurrent(runtime, snapshot, signal)
        if (!delivery) break
        try {
          this.assertCurrent(runtime, snapshot, signal)
          const ack = await this.mutateDelivery(snapshot.web, delivery, () => this.assertCurrent(runtime, snapshot, signal))
          this.assertCurrent(runtime, snapshot, signal)
          await this.options.transport.ackDelivery(runtime.target, withAdapterId(ack, this.options.adapterId), runtime.lease.leaseId, signal)
          this.assertCurrent(runtime, snapshot, signal)
        } catch (error) {
          if (error instanceof StaleRuntimeError) return
          this.assertCurrent(runtime, snapshot, signal)
          await this.options.transport.ackDelivery(runtime.target, withAdapterId({
            id: delivery.id,
            outcome: "uncertain",
            reason: error instanceof Error ? error.message : String(error),
          }, this.options.adapterId), runtime.lease.leaseId, signal)
          this.assertCurrent(runtime, snapshot, signal)
          continue
        }
      }
    } catch (error) {
      if (error instanceof StaleRuntimeError) return
      if (error instanceof LeaseStaleError) {
        await this.removeRuntime(runtime)
        return
      }
      throw error
    } finally {
      runtime.draining = false
      if (runtime.drainRequested) {
        runtime.drainRequested = false
        if (this.isActive(runtime, signal)) await this.drain(runtime, signal)
      }
    }
  }

  private async mutateDelivery(web: SlackWebClient, delivery: Delivery, ensureCurrent: () => void): Promise<DeliveryAck> {
    ensureCurrent()
    const payload = parseDeliveryPayload(delivery.payloadJson)
    const operation = payload.operation ?? "post_message"
    if (!isKnownDeliveryOperation(operation)) {
      const ack = await this.reconcileForUnknownOperation(web, delivery, ensureCurrent)
      ensureCurrent()
      return ack
    }
    const segments = Array.isArray(payload.segments) && payload.segments.length > 1 ? payload.segments : undefined
    if (segments) {
      const ack = await this.deliverSegments(web, delivery, payload, segments, ensureCurrent)
      ensureCurrent()
      return ack
    }
    if (operation === "chat_update") {
      const target = payload.providerMessageIdentity
      if (!target) throw new Error("chat.update delivery has no provider message identity")
      ensureCurrent()
      const response = await web.chat.update?.({
        channel: target.conversationId,
        ts: target.messageTs,
        text: requiredText(payload),
        ...(payload.blocks ? { blocks: payload.blocks } : {}),
      })
      ensureCurrent()
      if (!response) throw new Error("Slack client does not support chat.update")
      if (response.ok === false) {
        const ack = await this.fallbackAfterUpdateFailure(web, delivery, payload, response.error ?? "Slack rejected chat.update", ensureCurrent)
        ensureCurrent()
        return ack
      }
      return delivered(delivery, { conversationId: target.conversationId, messageTs: response.ts ?? target.messageTs })
    }

    if (operation === "reaction_add" || operation === "reaction_remove") {
      const target = payload.targetMessageIdentity
      if (!target || !payload.reaction) throw new Error(`${operation} delivery is missing its target`)
      ensureCurrent()
      const response = await mutateReaction(web, operation, target, payload.reaction, ensureCurrent)
      ensureCurrent()
      if (!response) throw new Error("Slack client does not support reactions")
      if (response.ok === false) {
        if (!isUnsupportedReactionError(response.error))
          return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the reaction" }
        if (response.error === "missing_scope") {
          if (!payload.fallbackText || !payload.fallbackDispatchRef) return delivered(delivery)
          const ack = await this.postFallback(web, delivery, payload, response.error, ensureCurrent)
          ensureCurrent()
          return ack
        }
        const statusTarget = payload.statusDispatchRef
          ? await findStatusMessage(web, delivery.conversationId, payload.statusDispatchRef, ensureCurrent)
          : undefined
        ensureCurrent()
        if (statusTarget && statusTarget.messageTs !== target.messageTs) {
          ensureCurrent()
          const statusResponse = await mutateReaction(web, operation, statusTarget, payload.reaction, ensureCurrent)
          ensureCurrent()
          if (!statusResponse) throw new Error("Slack client does not support reactions")
          if (statusResponse.ok !== false)
            return delivered(delivery)
          if (!isUnsupportedReactionError(statusResponse.error))
            return { id: delivery.id, outcome: "retry", reason: statusResponse.error ?? "Slack rejected the reaction" }
        }
        if (operation === "reaction_remove")
          return delivered(delivery)
        if (payload.fallbackText && payload.fallbackDispatchRef) {
          const ack = await this.postFallback(web, delivery, payload, response.error ?? "Slack does not support reactions", ensureCurrent)
          ensureCurrent()
          return ack
        }
        return delivered(delivery)
      }
      return delivered(delivery)
    }

    if (operation === "upload_file") {
      const ack = await this.uploadFile(web, delivery, payload, ensureCurrent)
      ensureCurrent()
      return ack
    }

    const text = payload.text ?? (payload.blocks && payload.blocks.length > 0 ? "" : requiredText(payload))
    const existingStatus = payload.statusDispatchRef
      ? await findStatusMessage(web, delivery.conversationId, payload.statusDispatchRef, ensureCurrent)
      : undefined
    ensureCurrent()
    if (existingStatus && web.chat.update) {
      ensureCurrent()
      const response = await web.chat.update({
        channel: existingStatus.conversationId,
        ts: existingStatus.messageTs,
        text,
        ...(payload.blocks ? { blocks: payload.blocks } : {}),
      })
      ensureCurrent()
      if (response.ok === false)
        return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the status update" }
      return delivered(delivery, { conversationId: existingStatus.conversationId, messageTs: response.ts ?? existingStatus.messageTs })
    }
    ensureCurrent()
    const response = await web.chat.postMessage({
      channel: delivery.conversationId,
      text,
      ...(delivery.threadTs ? { thread_ts: delivery.threadTs } : {}),
      ...(payload.clientMessageId ? { client_msg_id: payload.clientMessageId } : {}),
      ...(payload.blocks ? { blocks: payload.blocks } : {}),
    })
    ensureCurrent()
    if (response.ok === false)
      return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the post" }
    return delivered(delivery, response.ts ? { conversationId: delivery.conversationId, messageTs: response.ts } : undefined)
  }

  private async deliverSegments(web: SlackWebClient, delivery: Delivery, payload: DeliveryPayload, segments: readonly string[], ensureCurrent: () => void): Promise<DeliveryAck> {
    const thread_ts = delivery.threadTs ?? undefined
    let firstIdentity: ProviderMessageIdentity | undefined
    for (let index = 0; index < segments.length; index++) {
      ensureCurrent()
      const response = await web.chat.postMessage({
        channel: delivery.conversationId,
        text: segments[index]!,
        ...(thread_ts ? { thread_ts } : {}),
        ...(index === 0 && payload.clientMessageId ? { client_msg_id: payload.clientMessageId } : {}),
      })
      ensureCurrent()
      if (response.ok === false)
        return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected a segmented post" }
      if (index === 0 && response.ts)
        firstIdentity = { conversationId: delivery.conversationId, messageTs: response.ts }
    }
    return delivered(delivery, firstIdentity)
  }

  private async uploadFile(web: SlackWebClient, delivery: Delivery, payload: DeliveryPayload, ensureCurrent: () => void): Promise<DeliveryAck> {
    if (!web.filesUploadV2 || !payload.fileName || !payload.fileContentBase64)
      throw new Error("upload_file delivery is missing the Slack upload client or file payload")
    ensureCurrent()
    const response = await web.filesUploadV2({
      ...(delivery.threadTs
        ? { channels: delivery.conversationId, thread_ts: delivery.threadTs }
        : { channel_id: delivery.conversationId }),
      filename: payload.fileName,
      file: Buffer.from(payload.fileContentBase64, "base64"),
      ...(payload.text ? { initial_comment: payload.text } : {}),
    })
    ensureCurrent()
    if (response.ok === false)
      return { id: delivery.id, outcome: "retry", reason: response.error ?? "Slack rejected the file upload" }
    const identity = await this.fileShareIdentity(web, delivery, response, ensureCurrent)
    ensureCurrent()
    return identity ? delivered(delivery, identity) : delivered(delivery)
  }

  private async fileShareIdentity(web: SlackWebClient, delivery: Delivery, response: SlackFileUploadResponse, ensureCurrent: () => void): Promise<ProviderMessageIdentity | undefined> {
    const file = response.files?.[0]?.files?.[0]
    const ts = file?.shares?.public?.[delivery.conversationId]?.[0]?.ts
      ?? file?.shares?.private?.[delivery.conversationId]?.[0]?.ts
    if (ts) return { conversationId: delivery.conversationId, messageTs: ts }
    if (!file?.id) return undefined
    ensureCurrent()
    const history = await web.conversations?.history?.({ channel: delivery.conversationId, limit: 200 })
    ensureCurrent()
    const share = history?.messages?.find(candidate =>
      candidate.files?.some(candidateFile => candidateFile.id === file.id) && candidate.ts)
    return share?.ts ? { conversationId: delivery.conversationId, messageTs: share.ts } : undefined
  }

  private async fallbackAfterUpdateFailure(web: SlackWebClient, delivery: Delivery, payload: DeliveryPayload, reason: string, ensureCurrent: () => void) {
    if (!payload.fallbackText || !payload.fallbackDispatchRef)
      return { id: delivery.id, outcome: "retry" as const, reason }
    const ack = await this.postFallback(web, delivery, payload, reason, ensureCurrent)
    ensureCurrent()
    return ack
  }

  private async postFallback(web: SlackWebClient, delivery: Delivery, payload: DeliveryPayload, reason: string, ensureCurrent: () => void): Promise<DeliveryAck> {
    ensureCurrent()
    const response = await web.chat.postMessage({
      channel: delivery.conversationId,
      text: payload.fallbackText ?? payload.text ?? "",
      ...(delivery.threadTs ? { thread_ts: delivery.threadTs } : {}),
      client_msg_id: payload.fallbackDispatchRef,
      ...(payload.blocks ? { blocks: payload.blocks } : {}),
    })
    ensureCurrent()
    if (response.ok === false)
      return { id: delivery.id, outcome: "retry", reason: response.error ?? reason }
    return delivered(delivery, response.ts ? { conversationId: delivery.conversationId, messageTs: response.ts } : undefined)
  }

  private async reconcile(web: SlackWebClient | undefined, delivery: Delivery, ensureCurrent: () => void): Promise<DeliveryAck> {
    ensureCurrent()
    const payload = parseDeliveryPayload(delivery.payloadJson)
    const target = payload.providerMessageIdentity ?? payload.targetMessageIdentity
    if ((payload.operation === "reaction_add" || payload.operation === "reaction_remove") && target && payload.reaction) {
      const response = await getReaction(web, target, ensureCurrent)
      ensureCurrent()
      if (!web || !response) return { id: delivery.id, outcome: "uncertain", reason: "Slack client cannot reconcile reactions" }
      if (response.ok === false) {
        if (!isUnsupportedReactionError(response.error))
          return { id: delivery.id, outcome: "uncertain", reason: response.error ?? "Slack reaction reconciliation failed" }
        if (!payload.fallbackText || !payload.fallbackDispatchRef) return delivered(delivery, target)
        const ack = await this.postFallback(web, delivery, payload, response.error ?? "Slack reaction reconciliation failed", ensureCurrent)
        ensureCurrent()
        return ack
      }
      const present = response.message?.reactions?.some(reaction => reaction.name === payload.reaction)
      const deliveredState = payload.operation === "reaction_add" ? present : !present
      return deliveredState ? delivered(delivery, target) : { id: delivery.id, outcome: "retry", reason: "provider_mutation_absent" }
    }

    ensureCurrent()
    const history = await web?.conversations?.history?.({
      channel: delivery.conversationId,
      ...(target ? { latest: target.messageTs, oldest: target.messageTs } : {}),
      inclusive: true,
      limit: 200,
    })
    ensureCurrent()
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
    if (payload.operation === "chat_update" && payload.fallbackText && payload.fallbackDispatchRef) {
      const fallbackWeb = web
      if (!fallbackWeb) return { id: delivery.id, outcome: "uncertain", reason: "Slack client cannot post fallback" }
      const ack = await this.postFallback(fallbackWeb, delivery, payload, "provider_mutation_absent", ensureCurrent)
      ensureCurrent()
      return ack
    }
    return { id: delivery.id, outcome: "retry", reason: "provider_mutation_absent" }
  }

  private async reconcileForUnknownOperation(web: SlackWebClient, delivery: Delivery, ensureCurrent: () => void): Promise<DeliveryAck> {
    const ack = await this.reconcile(web, delivery, ensureCurrent)
    ensureCurrent()
    return ack
  }

  private async drainUncertain(runtime: ConnectionRuntime, snapshot: RuntimeSnapshot, signal: AbortSignal) {
    if (!this.options.transport.claimUncertainDelivery) return
    while (!signal.aborted) {
      this.assertCurrent(runtime, snapshot, signal)
      const delivery = await this.options.transport.claimUncertainDelivery(runtime.target, runtime.lease.leaseId, this.options.adapterId, signal)
      this.assertCurrent(runtime, snapshot, signal)
      if (!delivery) return
      try {
        this.assertCurrent(runtime, snapshot, signal)
        const ack = await this.reconcile(snapshot.web, delivery, () => this.assertCurrent(runtime, snapshot, signal))
        this.assertCurrent(runtime, snapshot, signal)
        await this.options.transport.ackDelivery(runtime.target, withAdapterId(ack, this.options.adapterId), runtime.lease.leaseId, signal)
        this.assertCurrent(runtime, snapshot, signal)
        if (ack.outcome === "uncertain") return
      } catch (error) {
        if (error instanceof StaleRuntimeError) throw error
        if (error instanceof LeaseStaleError) throw error
        this.assertCurrent(runtime, snapshot, signal)
        await this.options.transport.ackDelivery(runtime.target, withAdapterId({
          id: delivery.id,
          outcome: "uncertain",
          reason: error instanceof Error ? error.message : String(error),
        }, this.options.adapterId), runtime.lease.leaseId, signal)
        this.assertCurrent(runtime, snapshot, signal)
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
  return value.kind === "manager"
}

export function normalizeSocketEvent(body: unknown): SlackEnvelope {
  const event = isRecord(body) && isRecord(body.event) ? body.event : body
  if (!isRecord(event)) throw new Error("Slack Socket Mode event is malformed")
  const apiAppId = stringValue(event.api_app_id) ?? stringValue(body, "api_app_id")
  const teamId = stringValue(event.team_id) ?? stringValue(body, "team_id")
  const conversationId = stringValue(event.channel)
  const messageTs = stringValue(event.ts) ?? stringValue(event.event_ts)
  if (!apiAppId || !teamId || !conversationId || !messageTs) throw new Error("Slack event is missing its stable identity")
  const senderSlackUserId = stringValue(event.user)
  return {
    eventType: stringValue(event.type) ?? "message",
    apiAppId,
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
  const apiAppId = stringValue(payload.api_app_id)
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
  if (!apiAppId || !team || !user || !conversationId || !messageTs || !interactionId || !actionId || !actionValue)
    throw new Error("Slack interaction is missing its stable identity")
  return {
    eventType: "block_actions",
    apiAppId,
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

function slackEventType(value: unknown): string {
  const interaction = interactionPayload(value)
  if (interaction) return stringValue(interaction.type) ?? "interactive"
  const event = isRecord(value) && isRecord(value.event) ? value.event : value
  return stringValue(event, "type") ?? "unknown"
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
  readonly fileName?: string
  readonly fileContentBase64?: string
  readonly segments?: readonly string[]
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

function isKnownDeliveryOperation(operation: unknown): operation is "post_message" | "chat_update" | "reaction_add" | "reaction_remove" | "upload_file" {
  return operation === "post_message"
    || operation === "chat_update"
    || operation === "reaction_add"
    || operation === "reaction_remove"
    || operation === "upload_file"
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
  ensureCurrent: () => void,
) {
  const method = operation === "reaction_add" ? web.reactions?.add : web.reactions?.remove
  try {
    ensureCurrent()
    const response = await method?.call(web.reactions, {
      channel: target.conversationId,
      name: reaction,
      timestamp: target.messageTs,
    })
    ensureCurrent()
    return response
  } catch (error) {
    ensureCurrent()
    const code = slackErrorCode(error)
    if (!code) throw error
    return { ok: false, error: code }
  }
}

async function getReaction(web: SlackWebClient | undefined, target: ProviderMessageIdentity, ensureCurrent: () => void) {
  try {
    ensureCurrent()
    const response = await web?.reactions?.get?.({
      channel: target.conversationId,
      timestamp: target.messageTs,
      full: true,
    })
    ensureCurrent()
    return response
  } catch (error) {
    ensureCurrent()
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
  ensureCurrent: () => void,
): Promise<ProviderMessageIdentity | undefined> {
  ensureCurrent()
  const history = await web.conversations?.history?.({ channel: conversationId, limit: 200 })
  ensureCurrent()
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
