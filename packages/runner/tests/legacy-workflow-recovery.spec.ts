import { describe, expect, it } from 'vitest'
import type { DispatchWorkItem } from '../src/core/types.js'
import { buildLegacyBoundary } from '../src/runtime/legacy-workflow-recovery.js'
import { WorkResultJournal } from '../src/runtime/work-result-journal.js'
import { withTestRunnerResources } from './support/test-resources.js'

function legacyWork(): DispatchWorkItem {
  return {
    workflowRunId: 'workflow-legacy',
    workId: 'task-legacy',
    taskRunId: 'attempt-legacy',
    workType: 'task',
    stage: 'build',
    ownerKind: 'workflow',
    runnerId: 'runner-legacy',
    workspaceId: 'workspace-legacy',
    workspaceGeneration: 4,
    workspaceHead: 'head-legacy',
    workspaceTree: 'tree-legacy',
    variables: { workspace: { path: '/workspace', branch: 'main' } },
  }
}

describe('legacy Workflow completion recovery', () => {
  it('builds one deterministic non-settling boundary-missing observation and never reuses the plain result', () => {
    const work = legacyWork()
    const first = buildLegacyBoundary(work, 'runner-legacy')
    const second = buildLegacyBoundary(work, 'runner-legacy')

    expect(first).toEqual(second)
    expect(first).toMatchObject({
      workspaceOutcome: 'unconfirmed',
      workspaceReason: 'boundary-missing',
      actionCompletion: {
        actionStarted: false,
        outcome: 'unknown',
        output: null,
        artifactUploadIds: [],
      },
      commitReceipt: {
        authoritative: false,
        reason: 'boundary-missing',
      },
    })
    expect(buildLegacyBoundary({ ...work, stage: null }, 'runner-legacy')).toBeNull()
  })

  it('keeps a completed legacy journal entry until the idempotent acknowledgement succeeds', async () => {
    const work = legacyWork()
    await withTestRunnerResources(async () => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      await journal.begin(work)
      await journal.complete(work, { status: 'completed', output: { mustNotReplay: true } })

      expect(journal.legacyWorkflowState(work)).toBe('completed')
      const boundary = buildLegacyBoundary(work, 'runner-legacy')
      expect(boundary?.actionCompletion.output).toBeNull()
      expect(journal.completed()[0]?.result).toEqual({ status: 'completed', output: { mustNotReplay: true } })

      const restarted = new WorkResultJournal('/runner')
      await restarted.load()
      expect(restarted.legacyWorkflowState(work)).toBe('completed')
    })
  })

  it('does not classify a non-Workflow completed result as a legacy boundary fence', async () => {
    await withTestRunnerResources(async () => {
      const journal = new WorkResultJournal('/runner')
      await journal.load()
      const work: DispatchWorkItem = {
        workflowRunId: '',
        workId: 'agent-job-result',
        workType: 'agent-job',
        ownerKind: 'agent-job',
        agentJobId: 'job-1',
      }
      await journal.begin(work)
      await journal.complete(work, { status: 'completed' })
      expect(journal.legacyWorkflowState(work)).toBeNull()
    })
  })

  it('keeps exact report identity stable across a lost acknowledgement retry', () => {
    const first = buildLegacyBoundary(legacyWork(), 'runner-legacy')
    const second = buildLegacyBoundary(legacyWork(), 'runner-legacy')
    expect(first?.fingerprint).toBe(second?.fingerprint)
    expect(first?.identity).toEqual(second?.identity)
  })
})
