import { describe, expect, it } from 'vitest'
import {
  NOTIFICATION_KINDS,
  NOTIFICATION_KIND_VALUES,
  isNotificationKind,
  parseNotificationKind,
} from './types'

describe('NOTIFICATION_KINDS', () => {
  it('exposes the non-failure Agent-result attention kind exactly once', () => {
    expect(Object.values(NOTIFICATION_KINDS)).toEqual([
      NOTIFICATION_KINDS.WorkflowFailed,
      NOTIFICATION_KINDS.AgentResultUnconfirmed,
      NOTIFICATION_KINDS.ApprovalRequested,
      NOTIFICATION_KINDS.IssueStarted,
      NOTIFICATION_KINDS.IssueCompleted,
    ])
  })

  it('matches the wire strings used by the server', () => {
    expect(NOTIFICATION_KINDS.WorkflowFailed).toBe('workflow_failed')
    expect(NOTIFICATION_KINDS.AgentResultUnconfirmed).toBe('agent_result_unconfirmed')
    expect(NOTIFICATION_KINDS.ApprovalRequested).toBe('approval_requested')
    expect(NOTIFICATION_KINDS.IssueStarted).toBe('issue_started')
    expect(NOTIFICATION_KINDS.IssueCompleted).toBe('issue_completed')
  })
})

describe('NOTIFICATION_KIND_VALUES', () => {
  it('lists every NotificationKind value', () => {
    expect(new Set(NOTIFICATION_KIND_VALUES)).toEqual(new Set(Object.values(NOTIFICATION_KINDS)))
  })
})

describe('isNotificationKind', () => {
  it('returns true for every known kind', () => {
    expect(isNotificationKind('workflow_failed')).toBe(true)
    expect(isNotificationKind('agent_result_unconfirmed')).toBe(true)
    expect(isNotificationKind('approval_requested')).toBe(true)
    expect(isNotificationKind('issue_started')).toBe(true)
    expect(isNotificationKind('issue_completed')).toBe(true)
  })

  it('returns false for unknown kinds', () => {
    expect(isNotificationKind('something_else')).toBe(false)
    expect(isNotificationKind('')).toBe(false)
    expect(isNotificationKind('WORKFLOW_FAILED')).toBe(false)
  })
})

describe('parseNotificationKind', () => {
  it('returns the matching kind for a known string', () => {
    expect(parseNotificationKind('workflow_failed')).toBe(NOTIFICATION_KINDS.WorkflowFailed)
    expect(parseNotificationKind('agent_result_unconfirmed')).toBe(NOTIFICATION_KINDS.AgentResultUnconfirmed)
    expect(parseNotificationKind('approval_requested')).toBe(NOTIFICATION_KINDS.ApprovalRequested)
    expect(parseNotificationKind('issue_started')).toBe(NOTIFICATION_KINDS.IssueStarted)
    expect(parseNotificationKind('issue_completed')).toBe(NOTIFICATION_KINDS.IssueCompleted)
  })

  it('falls back to WorkflowFailed for unknown strings', () => {
    expect(parseNotificationKind('unknown')).toBe(NOTIFICATION_KINDS.WorkflowFailed)
    expect(parseNotificationKind('')).toBe(NOTIFICATION_KINDS.WorkflowFailed)
  })

  it('falls back to WorkflowFailed for null/undefined', () => {
    expect(parseNotificationKind(null)).toBe(NOTIFICATION_KINDS.WorkflowFailed)
    expect(parseNotificationKind(undefined)).toBe(NOTIFICATION_KINDS.WorkflowFailed)
  })
})