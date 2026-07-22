import { describe, it, expect } from 'vitest'
import { AGENT_DETAIL_EVENTS } from '../../entities/agent'
import type { EventName } from '../../entities/issue'
import {
  EVENT_TYPES,
  LEGACY_AGENT_DETAIL_EVENT_TYPES,
  TRANSCRIPT_EVENT_TYPES,
  REVERSE_DNS_EVENT_TYPES,
  CanonicalEventType,
} from './canonical-event-types'

describe('canonical event types', () => {
  it('EVENT_TYPES is a non-empty list', () => {
    expect(EVENT_TYPES.length).toBeGreaterThan(0)
  })

  it('includes every legacy snake_case name from AGENT_DETAIL_EVENTS', () => {
    for (const name of LEGACY_AGENT_DETAIL_EVENT_TYPES) {
      expect(EVENT_TYPES).toContain(name)
    }
  })

  it('includes the transcript event types', () => {
    expect(TRANSCRIPT_EVENT_TYPES).toEqual([
      'session.input',
      'message.delta',
      'reasoning.delta',
      'tool_call.started',
      'tool_call.updated',
      'tool_call.completed',
      'session.liveness',
      'usage.updated',
      'model.resolved',
      'session.closed',
      'session.followup_completed',
      'session.followup_failed',
      'compaction',
      'compaction_event',
      'context_health_update',
      'provider.retry',
    ])
    for (const name of TRANSCRIPT_EVENT_TYPES) {
      expect(EVENT_TYPES).toContain(name)
    }
  })

  it('includes the reverse-DNS names for workflow stage events', () => {
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.StageStarted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.StageCompleted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.StageFailed)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.StageApprovalRequested)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.StageApprovalResolved)
  })

  it('includes task lifecycle and artifact events used by live Activity', () => {
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.TaskStarted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.TaskCompleted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.TaskFailed)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.ArtifactRecorded)
  })

  it('includes the reverse-DNS names for workflow run events', () => {
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning)
  })

  it('includes the reverse-DNS names for issue lifecycle events', () => {
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueCreated)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueCancelled)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueArchived)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueUnarchived)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueReopened)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueWorkStarted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueCompleted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueDraftChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueWorkflowProfileChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueParentChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueRepositoryChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueCompositeStarted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.IssueCompositeStatusChanged)
  })

  it('does NOT contain the legacy IssueClosed or IssueWorkCompleted ids or constants', () => {
    const legacyIds = ['com.mohist.issue.closed', 'com.mohist.issue.work-completed']
    for (const legacy of legacyIds) {
      expect(EVENT_TYPES).not.toContain(legacy)
    }
    const registry = REVERSE_DNS_EVENT_TYPES as Record<string, string | undefined>
    expect(registry.IssueClosed).toBeUndefined()
    expect(registry.IssueWorkCompleted).toBeUndefined()
  })

  it('includes the reverse-DNS names for agent-session events', () => {
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionContextCompacted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionContextExhausted)
    expect(EVENT_TYPES).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated)
  })

  it('reverse-DNS names match the documented format', () => {
    expect(REVERSE_DNS_EVENT_TYPES.StageStarted).toBe('com.mohist.workflow.stage.started')
    expect(REVERSE_DNS_EVENT_TYPES.StageApprovalRequested).toBe('com.mohist.workflow.stage.approval-requested')
    expect(REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound).toBe('com.mohist.agent-session.runtime-bound')
  })

  it('AGENT_DETAIL_EVENTS matches the legacy agent-detail set plus transcript types and reverse-DNS agent-session names', () => {
    const expected = new Set<string>([
      ...LEGACY_AGENT_DETAIL_EVENT_TYPES,
      ...TRANSCRIPT_EVENT_TYPES,
      REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound,
      REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded,
      REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged,
      REVERSE_DNS_EVENT_TYPES.AgentSessionContextCompacted,
      REVERSE_DNS_EVENT_TYPES.AgentSessionContextExhausted,
      REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated,
    ])
    expect(new Set(AGENT_DETAIL_EVENTS as readonly string[])).toEqual(expected)
  })

  it('contains no duplicates', () => {
    const set = new Set(EVENT_TYPES)
    expect(set.size).toBe(EVENT_TYPES.length)
  })

  it('the type alias CanonicalEventType is exhaustive at the type level', () => {
    const sample: CanonicalEventType = REVERSE_DNS_EVENT_TYPES.StageStarted
    expect(EVENT_TYPES).toContain(sample)
  })

  it('EventName (union) is consistent with EVENT_TYPES (runtime list)', () => {
    const eventNameValues: readonly EventName[] = [
      ...LEGACY_AGENT_DETAIL_EVENT_TYPES,
      REVERSE_DNS_EVENT_TYPES.StageStarted,
      REVERSE_DNS_EVENT_TYPES.StageCompleted,
      REVERSE_DNS_EVENT_TYPES.StageFailed,
      REVERSE_DNS_EVENT_TYPES.StageApprovalRequested,
      REVERSE_DNS_EVENT_TYPES.StageApprovalResolved,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning,
      REVERSE_DNS_EVENT_TYPES.IssueCreated,
      REVERSE_DNS_EVENT_TYPES.IssueCancelled,
      REVERSE_DNS_EVENT_TYPES.IssueArchived,
      REVERSE_DNS_EVENT_TYPES.IssueUnarchived,
      REVERSE_DNS_EVENT_TYPES.IssueReopened,
      REVERSE_DNS_EVENT_TYPES.IssueWorkStarted,
      REVERSE_DNS_EVENT_TYPES.IssueCompleted,
      REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged,
      REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged,
      REVERSE_DNS_EVENT_TYPES.IssueDraftChanged,
      REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded,
      REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved,
      REVERSE_DNS_EVENT_TYPES.IssueWorkflowProfileChanged,
      REVERSE_DNS_EVENT_TYPES.IssueParentChanged,
      REVERSE_DNS_EVENT_TYPES.IssueRepositoryChanged,
      REVERSE_DNS_EVENT_TYPES.IssueCompositeStarted,
      REVERSE_DNS_EVENT_TYPES.IssueCompositeStatusChanged,
      REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound,
      REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded,
      REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged,
      REVERSE_DNS_EVENT_TYPES.AgentSessionContextCompacted,
      REVERSE_DNS_EVENT_TYPES.AgentSessionContextExhausted,
      REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated,
    ] as readonly EventName[]
    for (const name of eventNameValues) {
      expect(EVENT_TYPES).toContain(name)
    }
  })
})
