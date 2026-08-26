import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import {
  promoteAndReportDurableJournalResults,
  retryDueReports,
  type HostExecutionContext,
} from '../src/runtime/host-execution.js'
import { AWAITING_ACK_RETRY_INTERVAL_MS } from '../src/runtime/host-timing.js'
import { WorkResultJournal } from '../src/runtime/work-result-journal.js'

const work = {
  workflowRunId: 'wr-stale-retirement',
  workId: 'work-stale-retirement',
  taskRunId: 'task-stale-retirement',
  workType: 'task',
  uses: 'test/block',
  ownerKind: 'workflow',
  variables: { workspace: { path: '/virtual/mohist-runner-test' } },
} as never

async function journalWithCompletedResult() {
  const dir = mkdtempSync(join(tmpdir(), 'stale-report-retirement-'))
  const journal = new WorkResultJournal(dir, { filePath: join(dir, 'work-results.json') })
  await journal.load()
  await journal.begin(work)
  await journal.complete(work, { status: 'failed', message: 'failed before any turn started' })
  return journal
}

function makeContext(journal: WorkResultJournal, report: (...args: unknown[]) => unknown): HostExecutionContext {
  return {
    connection: { report: vi.fn(report), sendRecoveryReceipt: vi.fn() },
    receiptId: () => 'receipt-1',
    workResultJournal: journal,
    runtimeTurnRegistry: { get: () => null, remove: () => undefined },
    recoveredStartedWork: {},
    terminalTaskLogDelivery: {},
    terminalTaskLogDeliveryInFlight: new Set<string>(),
    syncOpenCodeWorkOwners: vi.fn(),
    inFlight: new Map(),
    awaitingAck: new Map(),
  } as unknown as HostExecutionContext
}

describe('work report stale retirement', () => {
  it('retires a promoted journal entry when the server answers with definitive stale', async () => {
    const journal = await journalWithCompletedResult()
    const report = vi.fn(async () => ({ tracked: false, reason: 'stale' }))
    const ctx = makeContext(journal, report)

    await promoteAndReportDurableJournalResults(ctx)
    expect(report).toHaveBeenCalledTimes(1)
    expect(ctx.awaitingAck.size).toBe(0)
    expect(journal.completed()).toHaveLength(0)
    expect(ctx.syncOpenCodeWorkOwners).toHaveBeenCalled()

    // The retired entry must not re-enter the retry loop.
    await retryDueReports(ctx)
    expect(report).toHaveBeenCalledTimes(1)
  })

  it('retires an awaiting-ack entry on stale instead of scheduling another retry', async () => {
    const journal = await journalWithCompletedResult()
    let attempts = 0
    const report = vi.fn(async () => {
      attempts += 1
      if (attempts === 1) throw new Error('transient transport failure')
      return { tracked: false, reason: 'stale' }
    })
    const ctx = makeContext(journal, report)

    await promoteAndReportDurableJournalResults(ctx)
    expect(attempts).toBe(1)
    const key = ctx.awaitingAck.keys().next().value as string
    const held = ctx.awaitingAck.get(key)!
    held.entry.retryAt = Date.now() - 1

    await retryDueReports(ctx)
    expect(attempts).toBe(2)
    expect(ctx.awaitingAck.size).toBe(0)
    expect(journal.completed()).toHaveLength(0)

    held.entry.retryAt = Date.now() - 1
    await retryDueReports(ctx)
    expect(attempts).toBe(2)
    void AWAITING_ACK_RETRY_INTERVAL_MS
  })
})
