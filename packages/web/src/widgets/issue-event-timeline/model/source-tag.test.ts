import { describe, expect, it } from 'vitest'
import { classifySource } from './source-tag'

describe('classifySource', () => {
  it('classifies com.mohist.issue.* events as ISSUE', () => {
    expect(classifySource('com.mohist.issue.created')).toBe('ISSUE')
    expect(classifySource('com.mohist.issue.labels-changed')).toBe('ISSUE')
    expect(classifySource('com.mohist.issue.priority-changed')).toBe('ISSUE')
  })

  it('classifies comment_added as ISSUE', () => {
    expect(classifySource('comment_added')).toBe('ISSUE')
  })

  it('classifies com.mohist.workflow.* events as WORKFLOW', () => {
    expect(classifySource('com.mohist.workflow.run.started')).toBe('WORKFLOW')
    expect(classifySource('com.mohist.workflow.stage.started')).toBe('WORKFLOW')
    expect(classifySource('com.mohist.workflow.stage.approval-requested')).toBe('WORKFLOW')
  })

  it('classifies legacy workflow events as WORKFLOW', () => {
    expect(classifySource('rebase_conflict')).toBe('WORKFLOW')
    expect(classifySource('merge_completed')).toBe('WORKFLOW')
    expect(classifySource('check_update')).toBe('WORKFLOW')
    expect(classifySource('integration_failed')).toBe('WORKFLOW')
    expect(classifySource('agent_started')).toBe('WORKFLOW')
    expect(classifySource('base_drift_detected')).toBe('WORKFLOW')
  })
})
