import type {
  AdapterLease,
  AdapterTransport,
  Delivery,
  DeliveryAck,
  IngressResult,
  RuntimeLease,
  SlackAdapterTarget,
  SlackEnvelope,
  SlackFileUploadResponse,
  SlackWebClient,
  SocketClient,
  SocketClientFactory,
  WebClientFactory,
} from './types.js'
import { LeaseStaleError } from './transport.js'
import { slackLogger } from './logger.js'
import {
  connectionKey,
  isSlackInteraction,
  normalizeSlackInteraction,
  normalizeSocketEvent,
  slackEventType,
} from './adapter-events.js'
import { mutateDelivery, reconcile, withAdapterId } from './adapter-delivery.js'

export { normalizeSlackInteraction, normalizeSocketEvent } from './adapter-events.js'

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

class StaleRuntimeError extends Error {}

export class SlackAdapter {
  private readonly log = slackLogger.child('adapter')
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
    signal.addEventListener('abort', () => void this.stop(), { once: true })
    await this.refreshConnections(signal)
    const discoveryMs = Math.max(1_000, this.options.discoveryIntervalMs ?? 15_000)
    this.discoveryTimer = setInterval(
      () =>
        void this.refreshConnections(signal).catch((error) => {
          this.log.error('target discovery failed', { reason: safeErrorMessage(error) })
        }),
      discoveryMs,
    )
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
    await Promise.all(
      targets.map(async (ref) => {
        const key = connectionKey(ref)
        if (this.runtimes.has(key)) return
        try {
          const validationLease = await this.options.transport.acquireLease(
            ref,
            'validation',
            this.options.adapterId,
            signal,
          )
          if (validationLease && validationLease.kind === 'validation')
            await this.validate(ref, validationLease, signal)
          const lease = await this.options.transport.acquireLease(ref, 'runtime', this.options.adapterId, signal)
          if (!lease || lease.kind !== 'runtime') return
          const runtime: ConnectionRuntime = {
            target: ref,
            generation: 0,
            lease,
            draining: false,
            drainRequested: false,
          }
          this.runtimes.set(key, runtime)
          try {
            await this.startRuntime(runtime, signal)
          } catch (error) {
            await this.removeRuntime(runtime)
            throw error
          }
        } catch (error) {
          this.log.error('target connection failed', { target: key, reason: safeErrorMessage(error) })
          this.runtimes.delete(key)
        }
      }),
    )
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
      this.log.error('socket disconnect failed', {
        target: connectionKey(target),
        reason: safeErrorMessage(error),
      })
    }
  }

  private async validate(
    target: SlackAdapterTarget,
    lease: Extract<AdapterLease, { kind: 'validation' }>,
    signal: AbortSignal,
  ) {
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
    if (!(await this.openRuntimeSocket(runtime, signal, runtime.generation))) return
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

  private async openRuntimeSocket(
    runtime: ConnectionRuntime,
    signal: AbortSignal,
    generation: number,
  ): Promise<boolean> {
    const web = this.options.webFactory(runtime.lease.botToken, runtime.target)
    const socket = this.options.socketFactory(runtime.lease.appToken, runtime.target)
    this.observeSocket(socket, runtime.target)
    socket.on('slack_event', async (event) => {
      const interaction = isSlackInteraction(event.body)
      const eventType = slackEventType(event.body)
      const target = connectionKey(runtime.target)
      if (this.stopped || signal.aborted || this.runtimes.get(target) !== runtime || runtime.socket !== socket) return
      this.log.info('envelope received', { target, event: eventType })
      try {
        await this.handleEvent(runtime, event.body, event.ack, eventType, signal)
      } catch (error) {
        this.log.error(
          interaction
            ? 'interaction processing failed after acknowledgement'
            : 'event handling failed before acknowledgement',
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
      const renewal = await this.options.transport.renewLease(
        runtime.target,
        currentLease.leaseId,
        this.options.adapterId,
        signal,
      )
      if (!this.isActive(runtime, signal) || runtime.generation !== generation) return
      if (!renewal || renewal.kind !== 'runtime' || renewal.leaseId !== currentLease.leaseId) {
        await this.removeRuntime(runtime)
        return
      }
      runtime.lease = { ...currentLease, generation: renewal.generation, expiresAt: renewal.expiresAt }
      await this.drain(runtime, signal)
    } catch (error) {
      if (this.stopped || signal.aborted) return
      this.log.error('target lease refresh failed', {
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
    return (
      this.isGenerationCurrent(runtime, snapshot.generation, signal) &&
      runtime.socket === snapshot.socket &&
      runtime.web === snapshot.web
    )
  }

  private assertCurrent(runtime: ConnectionRuntime, snapshot: RuntimeSnapshot, signal: AbortSignal): void {
    if (!this.isCurrent(runtime, snapshot, signal)) throw new StaleRuntimeError()
  }

  private observeSocket(socket: SocketClient, target: SlackAdapterTarget) {
    const key = connectionKey(target)
    for (const state of ['connecting', 'connected', 'reconnecting', 'disconnected'] as const) {
      socket.onState?.(state, () => this.log.info('socket state changed', { target: key, state }))
    }
    socket.onState?.('error', (error) => {
      this.log.error('socket failed', { target: key, reason: safeErrorMessage(error) })
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
      this.log.info('envelope forwarding', { target, event: eventType })
      if (interaction) {
        this.assertCurrent(runtime, snapshot, signal)
        await this.options.transport.interaction(
          runtime.target,
          normalizeSlackInteraction(body),
          runtime.lease.leaseId,
          this.options.adapterId,
          signal,
        )
        this.assertCurrent(runtime, snapshot, signal)
        this.log.info('interaction forwarded', { target, event: eventType })
        await this.drain(runtime, signal)
        return
      }
      const envelope = normalizeSocketEvent(body)
      this.assertCurrent(runtime, snapshot, signal)
      const result = await this.options.transport.ingress(
        runtime.target,
        envelope,
        runtime.lease.leaseId,
        this.options.adapterId,
        signal,
      )
      this.assertCurrent(runtime, snapshot, signal)
      this.log.info('ingress accepted', { target, event: eventType, kind: result.kind })
      if (!(await this.renderUserFacingRejection(runtime, snapshot, envelope, result, signal))) return
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
    if (result.kind !== 'backpressured') return this.isCurrent(runtime, snapshot, signal)
    if (runtime.draining) return this.isCurrent(runtime, snapshot, signal)
    if (!this.isCurrent(runtime, snapshot, signal)) return false
    const reason = result.reason ?? 'This Slack Connection is backpressured; retry after pending deliveries drain.'
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
        const delivery = await this.options.transport.claimDelivery(
          runtime.target,
          runtime.lease.leaseId,
          this.options.adapterId,
          signal,
        )
        this.assertCurrent(runtime, snapshot, signal)
        if (!delivery) break
        try {
          this.assertCurrent(runtime, snapshot, signal)
          const ack = await mutateDelivery(snapshot.web, delivery, () => this.assertCurrent(runtime, snapshot, signal))
          this.assertCurrent(runtime, snapshot, signal)
          await this.options.transport.ackDelivery(
            runtime.target,
            withAdapterId(ack, this.options.adapterId),
            runtime.lease.leaseId,
            signal,
          )
          this.assertCurrent(runtime, snapshot, signal)
        } catch (error) {
          if (error instanceof StaleRuntimeError) return
          this.assertCurrent(runtime, snapshot, signal)
          await this.options.transport.ackDelivery(
            runtime.target,
            withAdapterId(
              {
                id: delivery.id,
                outcome: 'uncertain',
                reason: error instanceof Error ? error.message : String(error),
              },
              this.options.adapterId,
            ),
            runtime.lease.leaseId,
            signal,
          )
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

  private async drainUncertain(runtime: ConnectionRuntime, snapshot: RuntimeSnapshot, signal: AbortSignal) {
    if (!this.options.transport.claimUncertainDelivery) return
    while (!signal.aborted) {
      this.assertCurrent(runtime, snapshot, signal)
      const delivery = await this.options.transport.claimUncertainDelivery(
        runtime.target,
        runtime.lease.leaseId,
        this.options.adapterId,
        signal,
      )
      this.assertCurrent(runtime, snapshot, signal)
      if (!delivery) return
      try {
        this.assertCurrent(runtime, snapshot, signal)
        const ack = await reconcile(snapshot.web, delivery, () => this.assertCurrent(runtime, snapshot, signal))
        this.assertCurrent(runtime, snapshot, signal)
        await this.options.transport.ackDelivery(
          runtime.target,
          withAdapterId(ack, this.options.adapterId),
          runtime.lease.leaseId,
          signal,
        )
        this.assertCurrent(runtime, snapshot, signal)
        if (ack.outcome === 'uncertain') return
      } catch (error) {
        if (error instanceof StaleRuntimeError) throw error
        if (error instanceof LeaseStaleError) throw error
        this.assertCurrent(runtime, snapshot, signal)
        await this.options.transport.ackDelivery(
          runtime.target,
          withAdapterId(
            {
              id: delivery.id,
              outcome: 'uncertain',
              reason: error instanceof Error ? error.message : String(error),
            },
            this.options.adapterId,
          ),
          runtime.lease.leaseId,
          signal,
        )
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

function safeErrorMessage(error: unknown) {
  const message = error instanceof Error ? error.message : String(error)
  return message.replace(/(?:xapp|xoxb|xoxp|xoxe)[.A-Za-z0-9_-]*/gi, '<redacted>')
}
