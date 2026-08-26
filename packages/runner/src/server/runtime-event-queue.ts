import type { AgentSessionRuntimeEventReceipt } from './connection.js'
import { runtimeEventDeliveryKey, runtimeEventSchedulingKey } from './runtime-event-queue-identity.js'

export type RuntimeEventAcknowledgementPolicy = 'matching-receipt' | 'successful-response'

export interface RuntimeEventRecord {
  readonly id: string
  readonly producerFamily:
    | 'workflow-session'
    | 'workflow-cleanup'
    | 'session-followup'
    | 'generic-followup'
    | 'binding-reconcile'
  readonly target: RuntimeEventTarget
  readonly runtimeSessionId: string
  readonly runtime?: string | null
  readonly sessionTurnId?: string | null
  readonly work: RuntimeEventWorkMetadata | null
  readonly event: RuntimeEventEntry
  readonly acknowledgementPolicy: RuntimeEventAcknowledgementPolicy
}

export type RuntimeEventTarget =
  | { kind: 'workflow'; projectId: string; workflowRunId: string; sessionName: string }
  | { kind: 'generic'; projectId: string; sessionId: string }
  | { kind: 'session'; sessionId: string }

export interface RuntimeEventWorkMetadata {
  readonly workId: string
  readonly workType: string
  readonly stage: string | null
  readonly taskRunId?: string | null
  readonly runnerId?: string | null
  readonly agentSessionId?: string | null
  readonly inputDeliveryId?: string | null
  readonly agentTurnId?: string | null
}

export interface RuntimeEventEntry {
  readonly type: string
  readonly payload: Record<string, unknown>
}

export interface RuntimeEventDelivery {
  send(record: RuntimeEventRecord, signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[]>
  sendBatch?(records: readonly RuntimeEventRecord[], signal: AbortSignal): Promise<AgentSessionRuntimeEventReceipt[][]>
}

export interface AgentSessionRuntimeEventQueueOptions {
  readonly deliver?: RuntimeEventDelivery
  readonly retryDelayMs?: number
  readonly deliveryBatchSize?: number
}

export interface AgentSessionRuntimeEventQueue {
  ready(): boolean
  load(): Promise<void>
  recover(): Promise<void>
  enqueueBeforeExecution(record: RuntimeEventRecord): Promise<void>
  awaitInputReceipt?(recordId: string): Promise<AgentSessionRuntimeEventReceipt>
  enqueueProducedFact(record: RuntimeEventRecord): Promise<void>
  enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void>
  kick(): Promise<void>
  stop(): Promise<void>
  snapshot(): readonly RuntimeEventRecord[]
}

export class AlreadyConsumedRuntimeEventError extends Error {
  readonly classification = 'already-consumed' as const
  constructor(readonly recordId: string) {
    super(`runtime-event record ${recordId} was already consumed`)
    this.name = 'AlreadyConsumedRuntimeEventError'
  }
}

const DEFAULT_RETRY_DELAY_MS = 2_000
const DEFAULT_DELIVERY_BATCH_SIZE = 64

export function createAgentSessionRuntimeEventQueue(
  options: AgentSessionRuntimeEventQueueOptions = {},
): AgentSessionRuntimeEventQueue {
  return new InMemoryRuntimeEventQueue(options)
}

class InMemoryRuntimeEventQueue implements AgentSessionRuntimeEventQueue {
  private readonly deliver: RuntimeEventDelivery
  private readonly retryDelayMs: number
  private readonly deliveryBatchSize: number
  private readonly records: RuntimeEventRecord[] = []
  private readonly inputReceipts = new Map<string, AgentSessionRuntimeEventReceipt>()
  private readonly inputWaiters = new Map<
    string,
    { resolve(receipt: AgentSessionRuntimeEventReceipt): void; reject(error: unknown): void }
  >()
  private draining: Promise<void> | null = null
  private retry: ReturnType<typeof setTimeout> | null = null
  private stopped = false
  private readonly stopController = new AbortController()

  constructor(options: AgentSessionRuntimeEventQueueOptions) {
    this.deliver = options.deliver ?? { send: async () => [] }
    this.retryDelayMs = options.retryDelayMs ?? DEFAULT_RETRY_DELAY_MS
    this.deliveryBatchSize = Math.max(1, options.deliveryBatchSize ?? DEFAULT_DELIVERY_BATCH_SIZE)
  }

  ready(): boolean {
    return !this.stopped
  }

  async load(): Promise<void> {}

  async recover(): Promise<void> {
    await this.kick()
  }

  snapshot(): readonly RuntimeEventRecord[] {
    return this.records.slice()
  }

  async enqueueBeforeExecution(record: RuntimeEventRecord): Promise<void> {
    this.enqueue(record)
  }

  async awaitInputReceipt(recordId: string): Promise<AgentSessionRuntimeEventReceipt> {
    const receipt = this.inputReceipts.get(recordId)
    if (receipt) {
      this.inputReceipts.delete(recordId)
      return receipt
    }
    if (this.stopped) throw new Error('runtime-event queue is stopped')
    return await new Promise<AgentSessionRuntimeEventReceipt>((resolve, reject) => {
      this.inputWaiters.set(recordId, { resolve, reject })
      void this.kick()
    })
  }

  async enqueueProducedFact(record: RuntimeEventRecord): Promise<void> {
    this.enqueue(record)
  }

  async enqueueProducedFactBatch(records: readonly RuntimeEventRecord[]): Promise<void> {
    for (const record of records) this.enqueue(record, false)
    void this.kick()
  }

  async kick(): Promise<void> {
    if (this.stopped) return
    if (!this.draining) {
      this.draining = this.drain().finally(() => {
        this.draining = null
      })
    }
    await this.draining
  }

  async stop(): Promise<void> {
    this.stopped = true
    this.stopController.abort()
    if (this.retry) clearTimeout(this.retry)
    this.retry = null
    for (const waiter of this.inputWaiters.values()) waiter.reject(new Error('runtime-event queue stopped'))
    this.inputWaiters.clear()
  }

  private enqueue(record: RuntimeEventRecord, kick = true): void {
    if (this.stopped) throw new Error('runtime-event queue is stopped')
    if (!this.records.some((candidate) => candidate.id === record.id)) this.records.push(record)
    if (kick) void this.kick()
  }

  private async drain(): Promise<void> {
    const attempted = new Set<string>()
    while (!this.stopped) {
      const head = this.records.find((record) => !attempted.has(runtimeEventSchedulingKey(record)))
      if (!head) break
      const schedulingKey = runtimeEventSchedulingKey(head)
      attempted.add(schedulingKey)
      const batch = this.records
        .filter((record) => runtimeEventSchedulingKey(record) === schedulingKey)
        .filter((record) => runtimeEventDeliveryKey(record) === runtimeEventDeliveryKey(head))
        .slice(0, this.deliveryBatchSize)
      try {
        const receipts = this.deliver.sendBatch
          ? await this.deliver.sendBatch(batch, this.stopController.signal)
          : await Promise.all(batch.map((record) => this.deliver.send(record, this.stopController.signal)))
        let progressed = false
        for (let index = 0; index < batch.length; index += 1) {
          const record = batch[index]!
          const recordReceipts = receipts[index] ?? []
          const matching = recordReceipts.find((receipt) => receipt.type === record.event.type)
          if (record.acknowledgementPolicy === 'matching-receipt' && !matching) break
          this.remove(record)
          progressed = true
          if (matching && (record.event.type === 'session.input' || record.event.type === 'session.cleanup')) {
            const waiter = this.inputWaiters.get(record.id)
            if (waiter) {
              this.inputWaiters.delete(record.id)
              waiter.resolve(matching)
            } else {
              this.inputReceipts.set(record.id, matching)
            }
          }
        }
        if (progressed) attempted.delete(schedulingKey)
      } catch {
        // Evidence delivery remains best effort. Keep the volatile suffix for
        // another attempt while this process lives.
      }
    }
    if (!this.stopped && this.records.length > 0 && !this.retry) {
      this.retry = setTimeout(() => {
        this.retry = null
        void this.kick()
      }, this.retryDelayMs)
      this.retry.unref?.()
    }
  }

  private remove(record: RuntimeEventRecord): void {
    const index = this.records.indexOf(record)
    if (index >= 0) this.records.splice(index, 1)
  }
}

export { runtimeEventDeliveryKey, runtimeEventSchedulingKey, workflowExecutionIdentity } from './runtime-event-queue-identity.js'
export type { WorkflowRuntimeEventExecutionIdentity } from './runtime-event-queue-identity.js'
