import {
  createAgentSessionRuntimeEventOutbox,
  RUNTIME_EVENT_OUTBOX_FILE,
  type AgentSessionRuntimeEventOutbox,
  type RuntimeEventDelivery,
  type RuntimeEventOutboxFileSystem,
  type RuntimeEventRecord,
} from "../../src/server/runtime-event-outbox.js"

export class RecordingFileSystem implements RuntimeEventOutboxFileSystem {
  readonly textStore = new Map<string, string>()
  readonly journal: Array<{ kind: "write"; path: string }> = []
  failNextWrite: (() => Error) | null = null
  failNextRead: (() => Error) | null = null

  async readText(path: string): Promise<string | null> {
    if (this.failNextRead) {
      const fail = this.failNextRead
      this.failNextRead = null
      throw fail()
    }
    return this.textStore.get(path) ?? null
  }

  async writeAtomicText(path: string, body: string): Promise<void> {
    if (this.failNextWrite) {
      const fail = this.failNextWrite
      this.failNextWrite = null
      throw fail()
    }
    this.textStore.set(path, body)
    this.journal.push({ kind: "write", path })
  }

  body(path: string): string | null {
    return this.textStore.get(path) ?? null
  }
}

export class BlockingWriteFileSystem extends RecordingFileSystem {
  readonly bodies: string[] = []
  writesStarted = 0
  activeWrites = 0
  maxConcurrentWrites = 0
  private readonly startWaiters: Array<() => void> = []
  private readonly releaseWaiters: Array<() => void> = []

  waitForNextWrite(): Promise<void> {
    return new Promise((resolve) => this.startWaiters.push(resolve))
  }

  releaseNextWrite(): void {
    const release = this.releaseWaiters.shift()
    if (!release) throw new Error("no blocked snapshot write")
    release()
  }

  override async writeAtomicText(path: string, body: string): Promise<void> {
    this.bodies.push(body)
    this.writesStarted += 1
    this.activeWrites += 1
    this.maxConcurrentWrites = Math.max(this.maxConcurrentWrites, this.activeWrites)
    this.startWaiters.shift()?.()
    await new Promise<void>((resolve) => this.releaseWaiters.push(resolve))
    try {
      await super.writeAtomicText(path, body)
    } finally {
      this.activeWrites -= 1
    }
  }
}

export function makeOutbox(options: {
  fileSystem?: RecordingFileSystem
  deliver?: RuntimeEventDelivery
  filePath?: string
  randomId?: () => string
  deliveryTimeoutMs?: number
  retryDelayMs?: number
  localRetryDelayMs?: number
  boundedConcurrency?: number
  deliveryBatchSize?: number
  maxRetentionEntries?: number
}) {
  const fileSystem = options.fileSystem ?? new RecordingFileSystem()
  const randomId = options.randomId ?? (() => `evt_${Math.random().toString(36).slice(2, 10)}`)
  const outbox: AgentSessionRuntimeEventOutbox = createAgentSessionRuntimeEventOutbox({
    fileSystem,
    deliver: options.deliver,
    filePath: options.filePath ?? RUNTIME_EVENT_OUTBOX_FILE,
    randomId,
    deliveryTimeoutMs: options.deliveryTimeoutMs ?? 100,
    retryDelayMs: options.retryDelayMs ?? 100,
    localRetryDelayMs: options.localRetryDelayMs ?? 100,
    boundedConcurrency: options.boundedConcurrency ?? 2,
    deliveryBatchSize: options.deliveryBatchSize,
    maxRetentionEntries: options.maxRetentionEntries,
  })
  return { outbox, fileSystem }
}

export function inputRecord(overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
  const id = overrides.id ?? "evt_input"
  return {
    id,
    producerFamily: "workflow-session",
    target: {
      kind: "workflow",
      projectId: "proj-1",
      workflowRunId: "wf-1",
      sessionName: "plan",
    },
    runtime: "opencode",
    runtimeSessionId: "ses_1",
    work: {
      workId: "work-1",
      taskRunId: "task-1.1",
      runnerId: "runner-1",
      agentSessionId: "agent-session-1",
      inputDeliveryId: id,
      agentTurnId: null,
      workType: "task",
      stage: "plan",
    },
    event: { type: "session.input", payload: { text: "do work" } },
    acknowledgementPolicy: "matching-receipt",
    ...overrides,
  }
}

export function followupTerminal(overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
  return {
    id: overrides.id ?? "evt_term",
    producerFamily: "generic-followup",
    target: { kind: "generic", projectId: "proj-1", sessionId: "gen-1" },
    runtimeSessionId: "ses_1",
    work: null,
    event: { type: "session.followup_completed", payload: { status: "completed", operationId: "op-1" } },
    acknowledgementPolicy: "successful-response",
    ...overrides,
  }
}

export function workflowFact(id: string, overrides: Partial<RuntimeEventRecord> = {}): RuntimeEventRecord {
  return {
    id,
    producerFamily: "workflow-session",
    target: { kind: "workflow", projectId: "proj-1", workflowRunId: "wf-1", sessionName: "build" },
    runtime: "opencode",
    runtimeSessionId: "runtime-1",
    work: {
      workId: "work-1",
      taskRunId: "task-1.1",
      runnerId: "runner-1",
      agentSessionId: "agent-session-1",
      inputDeliveryId: "input-1",
      agentTurnId: "turn-1",
      workType: "task",
      stage: "build",
    },
    event: { type: "message.delta", payload: { text: id } },
    acknowledgementPolicy: "matching-receipt",
    ...overrides,
  }
}

export async function flushMicrotasks(count = 4) {
  for (let i = 0; i < count; i += 1) {
    await Promise.resolve()
  }
}
