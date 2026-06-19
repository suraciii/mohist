import { describe, expect, it } from 'vitest'
import { describeEvent } from './describe'

describe('describeEvent', () => {
  it('describes stage transition with from and to', () => {
    expect(describeEvent('com.mohist.workflow.stage.started', { from: 'plan', to: 'build' }))
      .toBe('Stage moved from Plan to Build')
  })

  it('describes legacy stage_changed', () => {
    expect(describeEvent('stage_changed', { from: 'Plan', to: 'Code' }))
      .toBe('Stage moved from Plan to Code')
  })

  it('describes approval requested', () => {
    expect(describeEvent('com.mohist.workflow.stage.approval-requested', { stage: 'check' }))
      .toBe('Approval requested for Check')
  })

  it('describes issue labels changed with key=value map', () => {
    expect(describeEvent('com.mohist.issue.labels-changed', { labels: { stream: 'frontend', module: 'auth' } }))
      .toBe('Issue labeled module=auth, stream=frontend')
  })

  it('describes issue labels changed with both old and new maps', () => {
    expect(describeEvent('com.mohist.issue.labels-changed', {
      oldLabels: { stream: 'frontend' },
      labels: { stream: 'backend' },
    })).toBe('Issue labels changed from stream=frontend to stream=backend')
  })

  it('describes issue labels changed with empty payload as a generic change', () => {
    expect(describeEvent('com.mohist.issue.labels-changed', {}))
      .toBe('Issue labels changed')
  })

  it('describes rebase conflict with file count', () => {
    expect(describeEvent('rebase_conflict', { conflicts: ['a.ts', 'b.ts', 'c.ts'] }))
      .toBe('Rebase conflict detected on 3 files')
  })

  it('describes rebase conflict with one file', () => {
    expect(describeEvent('rebase_conflict', { conflicts: ['a.ts'] }))
      .toBe('Rebase conflict detected on 1 file')
  })

  it('describes merge completed', () => {
    expect(describeEvent('merge_completed', {}))
      .toBe('Merge completed')
  })

  it('describes merge failed with reason', () => {
    expect(describeEvent('merge_failed', { reason: 'merge conflict' }))
      .toBe('Merge failed: merge conflict')
  })

  it('describes run started', () => {
    expect(describeEvent('com.mohist.workflow.run.started', {}))
      .toBe('Run started')
  })

  it('describes run resumed', () => {
    expect(describeEvent('com.mohist.workflow.run.resumed', {}))
      .toBe('Run resumed')
  })

  it('describes issue created', () => {
    expect(describeEvent('com.mohist.issue.created', {}))
      .toBe('Issue created')
  })

  it('describes priority changed', () => {
    expect(describeEvent('com.mohist.issue.priority-changed', { priority: 'high' }))
      .toBe('Issue priority set to high')
  })

  it('describes comment added', () => {
    expect(describeEvent('comment_added', { body: 'Looks good' }))
      .toBe('Comment added: Looks good')
  })

  it('describes base drift needs attention', () => {
    expect(describeEvent('base_drift_detected', { decision: 'needs-attention' }))
      .toBe('Base drift needs attention')
  })

  it('falls back to prettified type for unknown events', () => {
    expect(describeEvent('com.mohist.workflow.unknown-event', {}))
      .toBe('Unknown Event')
  })

  it('falls back to generic stage changed when payload lacks fields', () => {
    expect(describeEvent('com.mohist.workflow.stage.started', {}))
      .toBe('Stage changed')
  })
})
