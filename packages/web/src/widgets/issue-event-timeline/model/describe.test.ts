import { describe, expect, it } from 'vitest'
import { describeEvent } from './describe'

describe('describeEvent', () => {
  it('describes stage transition with from and to', () => {
    expect(describeEvent('com.mohist.workflow.stage.started', { from: 'plan', to: 'build' })).toBe(
      'Stage moved from Plan to Build',
    )
  })

  it('describes approval requested', () => {
    expect(describeEvent('com.mohist.workflow.stage.approval-requested', { stage: 'check' })).toBe(
      'Approval requested for Check',
    )
  })

  it('describes issue labels changed with key=value map', () => {
    expect(describeEvent('com.mohist.issue.labels-changed', { labels: { stream: 'frontend', module: 'auth' } })).toBe(
      'Issue labeled module=auth, stream=frontend',
    )
  })

  it('describes issue labels changed with both old and new maps', () => {
    expect(
      describeEvent('com.mohist.issue.labels-changed', {
        oldLabels: { stream: 'frontend' },
        labels: { stream: 'backend' },
      }),
    ).toBe('Issue labels changed from stream=frontend to stream=backend')
  })

  it('describes issue labels changed with empty payload as a generic change', () => {
    expect(describeEvent('com.mohist.issue.labels-changed', {})).toBe('Issue labels changed')
  })

  it('describes run started', () => {
    expect(describeEvent('com.mohist.workflow.run.started', {})).toBe('Run started')
  })

  it('describes run resumed', () => {
    expect(describeEvent('com.mohist.workflow.run.resumed', {})).toBe('Run resumed')
  })

  it('describes issue created', () => {
    expect(describeEvent('com.mohist.issue.created', {})).toBe('Issue created')
  })

  it('describes issue cancelled under the canonical cancelled id', () => {
    expect(describeEvent('com.mohist.issue.cancelled', {})).toBe('Issue cancelled')
  })

  it('describes issue completed under the canonical completed id', () => {
    expect(describeEvent('com.mohist.issue.completed', {})).toBe('Issue completed')
  })

  it('does NOT recognise the legacy closed or work-completed ids as terminal timeline labels', () => {
    // The legacy ids are gone from the producer vocabulary; the timeline
    // describer must fall through to the prettified-type fallback for
    // any pre-rename row that may still be encountered (e.g. via a
    // backfilled read or a stray event).
    expect(describeEvent('com.mohist.issue.closed', {})).toBe('Closed')
    expect(describeEvent('com.mohist.issue.work-completed', {})).toBe('Work Completed')
  })

  it('describes priority changed', () => {
    expect(describeEvent('com.mohist.issue.priority-changed', { priority: 'high' })).toBe('Issue priority set to high')
  })

  it('falls back to prettified type for unknown events', () => {
    expect(describeEvent('com.mohist.workflow.unknown-event', {})).toBe('Unknown Event')
  })

  it('falls back to generic stage changed when payload lacks fields', () => {
    expect(describeEvent('com.mohist.workflow.stage.started', {})).toBe('Stage changed')
  })

  it.each([
    ['com.mohist.workflow.task.started', 'Build the feature started'],
    ['com.mohist.workflow.task.completed', 'Build the feature completed'],
    ['com.mohist.workflow.task.failed', 'Build the feature failed'],
  ])('names a resolved task subject for %s', (type, expected) => {
    expect(
      describeEvent(type, { stage: 'build', taskId: 'T-004' }, (stage, taskId) =>
        stage === 'build' && taskId === 'T-004' ? 'Build the feature' : null,
      ),
    ).toBe(expected)
  })

  it('falls back to the stable task ID when no title resolves', () => {
    expect(
      describeEvent('com.mohist.workflow.task.completed', { stage: 'build', taskId: 'T-legacy' }, () => null),
    ).toBe('T-legacy completed')
  })

  it('describes Agent-result attention without calling it a failure', () => {
    expect(describeEvent('com.mohist.workflow.agent-result-unconfirmed', { stage: 'build', taskId: 'T-007' })).toBe(
      'T-007 result unconfirmed',
    )
    expect(describeEvent('com.mohist.workflow.task.blocked', { stage: 'build', taskId: 'T-007' })).toBe(
      'T-007 blocked: Agent result unconfirmed',
    )
    expect(describeEvent('com.mohist.workflow.stage.blocked', { stage: 'build' })).toBe(
      'Stage Build blocked: Agent result unconfirmed',
    )
    expect(describeEvent('com.mohist.workflow.run.blocked')).toBe('Workflow blocked: Agent result unconfirmed')
  })

  it('names the artifact path in the visible summary', () => {
    expect(describeEvent('com.mohist.workflow.artifact.recorded', { path: 'artifacts/report.md' })).toBe(
      'artifacts/report.md recorded',
    )
  })
})
