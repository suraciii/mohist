import { describe, expect, it } from 'vitest'
import { classifyEvent } from './classify'
import type { TimelineCategory } from './types'

function expectCategory(type: string, payload: Record<string, unknown>, expected: TimelineCategory, attention: boolean) {
  const result = classifyEvent(type, payload)
  expect(result.category).toBe(expected)
  expect(result.attention).toBe(attention)
}

describe('classifyEvent priority ordering', () => {
  it('classifies Agent-result blocked events as actionable attention, not failure', () => {
    expectCategory('com.mohist.workflow.agent-result-unconfirmed', {}, 'attention', true)
    expectCategory('com.mohist.workflow.task.blocked', {}, 'attention', true)
    expectCategory('com.mohist.workflow.stage.blocked', {}, 'attention', true)
    expectCategory('com.mohist.workflow.run.blocked', {}, 'attention', true)
  })

  it('classifies failures before integration/success (merge_failed)', () => {
    expectCategory('merge_failed', {}, 'failure', true)
  })

  it('classifies rebase_conflict as failure, not integration', () => {
    expectCategory('rebase_conflict', { conflicts: ['a.ts'] }, 'failure', true)
  })

  it('classifies stage_failed as failure', () => {
    expectCategory('com.mohist.workflow.stage.failed', {}, 'failure', true)
  })

  it('classifies run_failed as failure', () => {
    expectCategory('com.mohist.workflow.run.failed', {}, 'failure', true)
  })

  it('classifies agent_error as failure', () => {
    expectCategory('agent_error', {}, 'failure', true)
  })

  it('classifies base_drift_detected with needs-attention as failure', () => {
    expectCategory('base_drift_detected', { decision: 'needs-attention' }, 'failure', true)
  })

  it('classifies merge_completed as success, not integration', () => {
    expectCategory('merge_completed', {}, 'success', false)
  })

  it('classifies stage_completed as success', () => {
    expectCategory('com.mohist.workflow.stage.completed', {}, 'success', false)
  })

  it('classifies run_completed as success', () => {
    expectCategory('com.mohist.workflow.run.completed', {}, 'success', false)
  })

  it('classifies approval_requested as approval with attention', () => {
    expectCategory('approval_requested', {}, 'approval', true)
  })

  it('classifies stage.approval-requested as approval with attention', () => {
    expectCategory('com.mohist.workflow.stage.approval-requested', {}, 'approval', true)
  })

  it('classifies approval-resolved as approval without attention', () => {
    expectCategory('com.mohist.workflow.stage.approval-resolved', {}, 'approval', false)
  })

  it('classifies agent_paused as approval with attention', () => {
    expectCategory('agent_paused', {}, 'approval', true)
  })

  it('classifies rebase_started as integration', () => {
    expectCategory('rebase_started', {}, 'integration', false)
  })

  it('classifies merge_started as integration', () => {
    expectCategory('merge_started', {}, 'integration', false)
  })

  it('classifies check_update as integration', () => {
    expectCategory('check_update', {}, 'integration', false)
  })

  it('classifies base_drift_detected without needs-attention as integration', () => {
    expectCategory('base_drift_detected', { decision: 'defer' }, 'integration', false)
  })

  it('classifies labels-changed as metadata', () => {
    expectCategory('com.mohist.issue.labels-changed', {}, 'metadata', false)
  })

  it('classifies priority-changed as metadata', () => {
    expectCategory('com.mohist.issue.priority-changed', {}, 'metadata', false)
  })

  it('classifies prerequisite events as metadata', () => {
    expectCategory('com.mohist.issue.prerequisite-added', {}, 'metadata', false)
    expectCategory('com.mohist.issue.prerequisite-removed', {}, 'metadata', false)
  })

  it('classifies comment_added as metadata', () => {
    expectCategory('comment_added', {}, 'metadata', false)
  })

  it('classifies run_started as workflow/lifecycle', () => {
    expectCategory('com.mohist.workflow.run.started', {}, 'workflow', false)
  })

  it('classifies stage_started as workflow/lifecycle', () => {
    expectCategory('com.mohist.workflow.stage.started', {}, 'workflow', false)
  })

  it('classifies unknown types as workflow/lifecycle (graceful fallback)', () => {
    expectCategory('com.mohist.workflow.unknown.thing', {}, 'workflow', false)
  })
})
