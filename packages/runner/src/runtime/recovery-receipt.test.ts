import { describe, expect, it } from 'vitest'
import type { DispatchWorkItem } from '../core/types.js'
import {
  createInterruptedRecoveryReceipt,
  createTerminalRecoveryReceipt,
  workResultFingerprint,
} from './recovery-receipt.js'
import { RuntimeTurnRegistry } from './runtime-turn-registry.js'

const work: DispatchWorkItem = {
  workflowRunId: 'workflow-1',
  taskRunId: 'task-1',
  workId: 'work-1',
  workType: 'task',
  ownerKind: 'workflow',
  uses: 'mohist/pi',
}

const binding = {
  agentSessionId: 'session-1',
  agentTurnId: 'turn-1',
  runtime: 'pi' as const,
  runtimeSessionId: '/workspace/session.jsonl',
  workDir: '/workspace',
}

describe('runtime recovery receipts', () => {
  it('createsImmutableTerminalAndInterruptionPayloadsWithTheFrozenIdentity', () => {
    const result = {
      status: 'completed',
      output: { answer: 'done' },
      cleanupAttempts: 4,
    }
    const terminal = createTerminalRecoveryReceipt(work, binding, 'runner-1', result, 'receipt-1')
    const interrupted = createInterruptedRecoveryReceipt(work, binding, 'runner-1', 'runner-update:1', 'receipt-2')

    expect(terminal).toMatchObject({
      workflowRunId: 'workflow-1',
      taskRunId: 'task-1',
      workId: 'work-1',
      agentSessionId: 'session-1',
      agentTurnId: 'turn-1',
      runtimeSessionId: '/workspace/session.jsonl',
      recoveryGeneration: 0,
      receiptId: 'receipt-1',
      payload: { type: 'terminal-result', result, fingerprint: expect.any(String) },
    })
    expect(terminal?.payload.type === 'terminal-result' && terminal.payload.fingerprint).toBe(
      workResultFingerprint(result),
    )
    expect(interrupted).toMatchObject({
      receiptId: 'receipt-2',
      payload: { type: 'update-interrupted', updateOperationId: 'runner-update:1', stopConfirmed: true },
    })
    expect(interrupted?.payload.type).toBe('update-interrupted')
    expect(interrupted && 'result' in interrupted.payload).toBe(false)
  })

  it('preservesExplicitReplacementGenerationInTheReceiptIdentity', () => {
    const replacement = createInterruptedRecoveryReceipt(
      { ...work, workId: 'work-1.recovery.1', recoveryGeneration: 1 },
      { ...binding, agentTurnId: 'turn-recovery-1' },
      'runner-1',
      'runner-update:1',
      'receipt-recovery-1',
    )

    expect(replacement).toMatchObject({
      workId: 'work-1.recovery.1',
      agentTurnId: 'turn-recovery-1',
      recoveryGeneration: 1,
    })
  })

  it('refusesReceiptsWithoutACompletePhysicalBinding', () => {
    expect(
      createInterruptedRecoveryReceipt(work, { ...binding, agentTurnId: null }, 'runner-1', 'op-1', 'r-1'),
    ).toBeNull()
    expect(
      createTerminalRecoveryReceipt(
        work,
        { ...binding, runtimeSessionId: null },
        'runner-1',
        { status: 'completed' },
        'r-2',
      ),
    ).toBeNull()
  })

  it('updatesTheBoundTurnWithoutReplacingItsWorkIdentity', () => {
    const registry = new RuntimeTurnRegistry()
    registry.register('workflow:workflow-1:work-1', { ...binding, runtimeSessionId: null })
    registry.update('workflow:workflow-1:work-1', {
      runtimeSessionId: '/workspace/session.jsonl',
      agentTurnId: 'turn-2',
    })
    expect(registry.get('workflow:workflow-1:work-1')).toEqual({ ...binding, agentTurnId: 'turn-2' })
    registry.remove('workflow:workflow-1:work-1')
    expect(registry.get('workflow:workflow-1:work-1')).toBeNull()
  })
})
