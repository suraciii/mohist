import type {
  AdapterLease,
  AdapterTransport,
  Delivery,
  DeliveryAck,
  IngressResult,
  LeaseRenewal,
  RuntimeLease,
  SlackAdapterTarget,
  SlackConnectionRef,
  SlackEnvelope,
  SlackHelloOutcome,
  SlackInteractionEnvelope,
  SlackLeaseKind,
  SlackManagerRef,
  SlackWebClient,
  SocketClient,
  SocketEvent,
} from './types.js'
import type { SlackLogFields, SlackLogger } from './logger.js'

export class FakeSocket implements SocketClient {
  private handler?: (event: SocketEvent) => Promise<void>
  started = false
  starts = 0
  disconnected = false
  acknowledged = false
  disconnectError?: Error
  disconnectGate?: Promise<void>
  disconnectStarted?: () => void
  startGate?: Promise<void>
  startStarted?: () => void

  on(_event: 'slack_event', handler: (event: SocketEvent) => Promise<void>) {
    this.handler = handler
  }

  async start() {
    this.started = true
    this.starts += 1
    this.startStarted?.()
    await this.startGate
    return { appId: 'A1' }
  }

  async emit(body: unknown) {
    this.acknowledged = false
    await this.handler?.({
      body,
      ack: () => {
        this.acknowledged = true
      },
    })
    return this.acknowledged
  }

  async disconnect() {
    this.disconnected = true
    this.disconnectStarted?.()
    await this.disconnectGate
    if (this.disconnectError) throw this.disconnectError
  }
}

export class RecordingLogger implements SlackLogger {
  readonly entries: Array<{ level: 'info' | 'error'; message: string; fields?: SlackLogFields }> = []

  info(message: string, fields?: SlackLogFields): void {
    this.entries.push({ level: 'info', message, fields })
  }

  error(message: string, fields?: SlackLogFields): void {
    this.entries.push({ level: 'error', message, fields })
  }

  child(): SlackLogger {
    return this
  }

  async flush(): Promise<void> {}
}

export class FakeTransport implements AdapterTransport {
  readonly leases: SlackAdapterTarget[] = []
  readonly envelopes: SlackEnvelope[] = []
  readonly interactions: SlackInteractionEnvelope[] = []
  readonly acks: Array<{ ref: SlackAdapterTarget; id: string; outcome: string }> = []
  readonly hellos: Array<{ ref: SlackAdapterTarget; leaseId: string; appId: string }> = []
  readonly deliveries: Delivery[] = [
    { id: 'delivery-1', conversationId: 'D1', threadTs: null, payloadJson: JSON.stringify({ text: 'accepted' }) },
  ]
  readonly uncertainDeliveries: Delivery[] = []
  connections: SlackAdapterTarget[] = []
  nextLeases: Array<AdapterLease | null> = []
  nextRenewals: Array<LeaseRenewal | null> = []
  nextIngressResults: IngressResult[] = []
  ingressError?: Error
  ingressGate?: Promise<void>
  ingressStarted?: () => void
  interactionError?: Error
  interactionGate?: Promise<void>
  interactionStarted?: () => void
  uncertainGate?: Promise<void>
  uncertainStarted?: () => void
  claimDeliveryCalls = 0
  leaseError?: Error

  async discover(): Promise<readonly SlackAdapterTarget[]> {
    return this.connections
  }

  async acquireLease(ref: SlackAdapterTarget, kind: SlackLeaseKind): Promise<AdapterLease | null> {
    if (this.leaseError) throw this.leaseError
    const lease =
      this.nextLeases.length > 0 ? this.nextLeases.shift()! : kind === 'validation' ? null : runtimeLease(ref)
    if (lease) this.leases.push(ref)
    return lease
  }

  async renewLease(ref: SlackAdapterTarget, leaseId: string): Promise<LeaseRenewal | null> {
    if (this.leaseError) throw this.leaseError
    return this.nextRenewals.length > 0
      ? this.nextRenewals.shift()!
      : { leaseId, kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' }
  }

  async reportHello(ref: SlackAdapterTarget, leaseId: string, appId: string): Promise<SlackHelloOutcome> {
    this.hellos.push({ ref, leaseId, appId })
    return 'verified'
  }

  async ingress(_ref: SlackAdapterTarget, envelope: SlackEnvelope): Promise<IngressResult> {
    this.envelopes.push(envelope)
    this.ingressStarted?.()
    await this.ingressGate
    if (this.ingressError) throw this.ingressError
    const queued = this.nextIngressResults.shift()
    return queued ?? { kind: 'accepted' }
  }

  async interaction(_ref: SlackAdapterTarget, envelope: SlackInteractionEnvelope) {
    this.interactions.push(envelope)
    this.interactionStarted?.()
    await this.interactionGate
    if (this.interactionError) throw this.interactionError
    return { state: 'stop_requested' }
  }

  async claimDelivery(): Promise<Delivery | null> {
    this.claimDeliveryCalls += 1
    return this.deliveries.shift() ?? null
  }

  async claimUncertainDelivery(): Promise<Delivery | null> {
    this.uncertainStarted?.()
    await this.uncertainGate
    return this.uncertainDeliveries.shift() ?? null
  }

  async ackDelivery(ref: SlackAdapterTarget, ack: DeliveryAck) {
    this.acks.push({
      ref,
      id: ack.id,
      outcome: ack.outcome,
      ...(ack.providerMessageIdentity ? { providerMessageIdentity: ack.providerMessageIdentity } : {}),
    })
  }
}

export function runtimeLease(ref: SlackAdapterTarget): RuntimeLease {
  return {
    kind: 'runtime',
    leaseId: `lease-${ref.kind === 'manager' ? ref.enrollmentId : ref.connectionId}`,
    generation: 1,
    expiresAt: '2026-01-01T00:05:00Z',
    appToken: `xapp-${ref.kind === 'manager' ? ref.enrollmentId : ref.connectionId}`,
    botToken: `xoxb-${ref.kind === 'manager' ? ref.enrollmentId : ref.connectionId}`,
  }
}

export class FakeWeb implements SlackWebClient {
  readonly posted: Array<{
    channel: string
    text: string
    thread_ts?: string
    client_msg_id?: string
    blocks?: readonly Record<string, unknown>[]
  }> = []
  readonly updated: Array<{ channel: string; ts: string; text: string; blocks?: readonly Record<string, unknown>[] }> =
    []
  readonly uploaded: Array<Record<string, unknown>> = []
  nextResponses: Array<{ ok?: boolean; error?: string }> = []
  nextUploadResponses: Array<{
    ok?: boolean
    error?: string
    files?: readonly {
      ok?: boolean
      error?: string
      files?: readonly {
        id?: string
        shares?: {
          public?: Record<string, readonly { ts?: string }[]>
          private?: Record<string, readonly { ts?: string }[]>
        }
      }[]
    }[]
  }> = []
  readonly chat = {
    postMessage: async (input: {
      channel: string
      text: string
      thread_ts?: string
      client_msg_id?: string
      blocks?: readonly Record<string, unknown>[]
    }) => {
      this.posted.push(input)
      const next = this.nextResponses.shift()
      return next ?? { ok: true }
    },
    update: async (input: {
      channel: string
      ts: string
      text: string
      blocks?: readonly Record<string, unknown>[]
    }) => {
      this.updated.push(input)
      return { ok: true, ts: input.ts }
    },
  }
  readonly filesUploadV2 = async (
    input:
      | { channel_id: string; filename?: string; file: Buffer; initial_comment?: string; alt_text?: string }
      | {
          channels: string
          thread_ts: string
          filename?: string
          file: Buffer
          initial_comment?: string
          alt_text?: string
        },
  ) => {
    this.uploaded.push({ ...input })
    const next = this.nextUploadResponses.shift()
    return next ?? { ok: true }
  }
}
