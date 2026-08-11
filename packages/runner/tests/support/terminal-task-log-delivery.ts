import type {
  TerminalTaskLogDeliveryIdentity,
  TerminalTaskLogDeliveryRecord,
  TerminalTaskLogDeliveryStore,
} from "../../src/runtime/terminal-task-log-delivery.js"

export class FakeTerminalTaskLogDeliveryStore implements TerminalTaskLogDeliveryStore {
  readonly records = new Map<string, TerminalTaskLogDeliveryRecord>()
  private loaded = false

  async load(): Promise<void> {
    this.loaded = true
  }

  ready(): boolean {
    return this.loaded
  }

  async listPending(): Promise<TerminalTaskLogDeliveryRecord[]> {
    return [...this.records.values()]
      .filter((record) => record.state === "pending")
      .map((record) => ({
        ...record,
        identity: { ...record.identity },
        batch: { ...record.batch, entries: record.batch.entries.map((entry) => ({ ...entry })) },
      }))
  }

  async putPending(record: Omit<TerminalTaskLogDeliveryRecord, "state" | "failure">): Promise<TerminalTaskLogDeliveryRecord> {
    const key = this.key(record.identity)
    const existing = this.records.get(key)
    if (existing) return existing
    const pending = { ...record, state: "pending" as const }
    this.records.set(key, pending)
    return pending
  }

  async acknowledge(identity: TerminalTaskLogDeliveryIdentity): Promise<void> {
    this.records.delete(this.key(identity))
  }

  async markFailed(identity: TerminalTaskLogDeliveryIdentity, failure: NonNullable<TerminalTaskLogDeliveryRecord["failure"]>): Promise<void> {
    const record = this.records.get(this.key(identity))
    if (!record) return
    record.state = "failed"
    record.failure = { ...failure }
  }

  private key(identity: TerminalTaskLogDeliveryIdentity): string {
    return `${identity.ownerKind}:${identity.ownerId}:${identity.workId}`
  }
}
