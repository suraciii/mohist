import { describe, expect, it } from 'vitest'
import { IssueHealth, IssueStatus, WorkflowStage, type RecoveryProjection } from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'
import type { WorkflowTimeline } from '../../../entities/issue/model/workflow-timeline'
import { computeActionsState, type ComputeActionsStateInput, type ErrorMessages } from './actionsState'

type IssueSubset = ComputeActionsStateInput['issue']

function makeIssue(overrides: Partial<IssueSubset> = {}): IssueSubset {
  return {
    number: 1,
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    workflowStage: null,
    workflowRunId: null,
    archivedAt: undefined,
    isDraft: false,
    blocker: null,
    blockedReason: undefined,
    recovery: undefined,
    ...overrides,
  }
}

function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    runnerAvailable: true,
    runnerMessage: null,
    capacity: { active: 0, max: 1 },
    ...overrides,
  }
}

function makeTimeline(overrides: Partial<WorkflowTimeline> = {}): WorkflowTimeline {
  return {
    workflowRunId: 'wr_1',
    status: 'running',
    currentStage: 'build',
    pendingWork: null,
    stages: [],
    availableActions: [],
    ...overrides,
  }
}

const noErrors: ErrorMessages = {
  closeError: null,
  reopenError: null,
  startError: null,
  rerunError: null,
  retryError: null,
}

describe('computeActionsState', () => {
  describe('archived note', () => {
    it('shows the archived note for an archived issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ archivedAt: '2026-06-25T10:00:00Z' }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showArchivedNote).toBe(true)
    })

    it('does not show the archived note for a non-archived issue', () => {
      const state = computeActionsState({
        issue: makeIssue(),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showArchivedNote).toBe(false)
    })

    it('suppresses every other action surface for an archived issue', () => {
      const state = computeActionsState({
        issue: makeIssue({
          archivedAt: '2026-06-25T10:00:00Z',
          health: IssueHealth.Done,
          status: IssueStatus.Done,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [
            { name: 'retry', label: 'Retry', target: null },
            { name: 'resume', label: 'Resume', target: null },
            { name: 'rerun', label: 'Rerun', target: null },
          ],
        }),
        errorMessages: noErrors,
      })
      expect(state.startVariant).toBeNull()
      expect(state.showForceStopPanel).toBe(false)
      expect(state.forceStopContext).toBeNull()
      expect(state.blockedActions.showRetry).toBe(false)
      expect(state.blockedActions.showResume).toBe(false)
      expect(state.blockedActions.showRerun).toBe(false)
      expect(state.blockedActions.showStop).toBe(false)
      expect(state.showStandaloneRerun).toBe(false)
      expect(state.showClose).toBe(false)
    })
  })

  describe('start variants (backlog)', () => {
    it('returns the draft variant when the issue is a draft', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.Backlog,
          isDraft: true,
          blocker: { kind: 'draft' },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toEqual({ kind: 'draft' })
    })

    it('returns the waiting-for variant when blocker.kind is waiting-for', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.Backlog,
          isDraft: false,
          blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toEqual({
        kind: 'waiting-for',
        issue: { number: 200, title: 'Foundational work' },
      })
    })

    it('returns the ready variant with all capacity gates for a ready backlog issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.Backlog, isDraft: false, blocker: null }),
        agentStatus: makeAgentStatus({
          runnerAvailable: true,
          capacity: { active: 0, max: 1 },
          activeAgents: [],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toEqual({
        kind: 'ready',
        runnerUnavailable: false,
        isAgentRunningOnThis: false,
        isCapacityFull: false,
        runnerMessage: null,
      })
    })

    it('flags runnerUnavailable when the agent has no runner', () => {
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.Backlog, isDraft: false, blocker: null }),
        agentStatus: makeAgentStatus({ runnerAvailable: false }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toMatchObject({ kind: 'ready', runnerUnavailable: true })
    })

    it('flags isCapacityFull when capacity is exhausted', () => {
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.Backlog, isDraft: false, blocker: null }),
        agentStatus: makeAgentStatus({ capacity: { active: 1, max: 1 } }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toMatchObject({ kind: 'ready', isCapacityFull: true })
    })

    it('flags isAgentRunningOnThis when an agent is already on this issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ number: 14, status: IssueStatus.Backlog, isDraft: false, blocker: null }),
        agentStatus: makeAgentStatus({ activeAgents: [{ issueId: 'i-14', issueNumber: 14, projectId: 'proj-1' }] }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toMatchObject({ kind: 'ready', isAgentRunningOnThis: true })
    })

    it('returns null startVariant for a non-backlog, non-archived issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.InProgress }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.startVariant).toBeNull()
    })
  })

  describe('force-stop panel visibility', () => {
    it('shows the force-stop panel when an agent is running on this issue', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
        }),
        agentStatus: makeAgentStatus({
          activeAgents: [{ issueId: 'i-1', issueNumber: 1, projectId: 'proj-1' }],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showForceStopPanel).toBe(true)
      expect(state.forceStopContext).not.toBeNull()
    })

    it('shows the force-stop panel when recovery allows wait', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: ['wait'],
          },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showForceStopPanel).toBe(true)
      expect(state.forceStopContext?.recoveryCanWait).toBe(true)
    })

    it('shows the force-stop panel when recovery allows stop', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: ['stop'],
          },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showForceStopPanel).toBe(true)
      expect(state.forceStopContext?.recoveryCanStop).toBe(true)
    })

    it('hides the force-stop panel when no agent runs and recovery does not allow wait/stop', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showForceStopPanel).toBe(false)
      expect(state.forceStopContext).toBeNull()
    })

    it('exposes the current agent progress in the force-stop context', () => {
      const progress = { stage: 'build', roundType: 'plan', roundIndex: 0, taskProgress: { completed: 1, total: 3 }, lastActivityAt: '2026-06-25T03:30:00Z' }
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active, number: 14 }),
        agentStatus: makeAgentStatus({
          activeAgents: [
            { issueId: 'i-14', issueNumber: 14, projectId: 'proj-1', progress },
          ],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.forceStopContext?.agentProgress).toEqual(progress)
    })
  })

  describe('blocked actions (blocked or interrupted health)', () => {
    it('shows retry when blocked and recovery/timeline allow retry', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'retry', label: 'Retry', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showRetry).toBe(true)
      expect(state.blockedActions.showResume).toBe(false)
      expect(state.blockedActions.showRerun).toBe(false)
      expect(state.blockedActions.showStop).toBe(false)
    })

    it('shows resume when blocked and allowedActions include resume', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'interrupted',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: ['resume'],
          },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showResume).toBe(true)
      expect(state.blockedActions.isInterrupted).toBe(true)
    })

    it('shows rerun when blocked and allowedActions include rerun', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showRerun).toBe(true)
    })

    it('shows stop when blocked and canStopWorkflow is true (workflowRunId set, not paused/done/cancelled)', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
          workflowRunId: 'wr_1',
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline(),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showStop).toBe(true)
    })

    it('hides stop when canStopWorkflow is false (no workflowRunId)', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
          workflowRunId: null,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline(),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showStop).toBe(false)
    })

    it('hides stop when canStopWorkflow is false (Done health)', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
          workflowRunId: 'wr_1',
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline(),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showStop).toBe(true)

      const doneState = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Done,
          workflowRunId: 'wr_1',
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline(),
        errorMessages: noErrors,
      })
      expect(doneState.blockedActions.showStop).toBe(false)
    })

    it('marks isInterrupted true when recoveryAttemptState is interrupted', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Interrupted,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'interrupted',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: [],
          },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.blockedActions.isInterrupted).toBe(true)
    })

    it('hides every blocked action when health is Active', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'interrupted',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: [],
          },
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [
            { name: 'retry', label: 'Retry', target: null },
            { name: 'resume', label: 'Resume', target: null },
            { name: 'rerun', label: 'Rerun', target: null },
          ],
        }),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showRetry).toBe(false)
      expect(state.blockedActions.showResume).toBe(false)
      expect(state.blockedActions.showRerun).toBe(false)
      expect(state.blockedActions.showStop).toBe(false)
      expect(state.blockedActions.isInterrupted).toBe(false)
    })

    it('keeps showProjectedCheckRepair false (preserves the showCheckRepairActions=false constant)', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [
            { name: 'retry', label: 'Retry', target: null },
            { name: 'rerun', label: 'Rerun', target: null },
          ],
        }),
        errorMessages: noErrors,
      })
      expect(state.blockedActions.showProjectedCheckRepair).toBe(false)
    })
  })

  describe('standalone rerun (not blocked, not interrupted, not backlog)', () => {
    it('shows standalone rerun when workflow stage, not blocked/interrupted, no agent running, canRerun', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.showStandaloneRerun).toBe(true)
    })

    it('hides standalone rerun for a backlog issue', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.Backlog,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.showStandaloneRerun).toBe(false)
    })

    it('hides standalone rerun for a Done issue', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.Done,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.showStandaloneRerun).toBe(false)
    })

    it('hides standalone rerun when an agent is running on this issue', () => {
      const state = computeActionsState({
        issue: makeIssue({
          number: 14,
          status: IssueStatus.InProgress,
          health: IssueHealth.Active,
          workflowStage: WorkflowStage.Build,
        }),
        agentStatus: makeAgentStatus({
          activeAgents: [{ issueId: 'i-14', issueNumber: 14, projectId: 'proj-1' }],
        }),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.showStandaloneRerun).toBe(false)
    })

    it('hides standalone rerun when the issue is Blocked', () => {
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Blocked,
          workflowStage: WorkflowStage.Build,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: makeTimeline({
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        }),
        errorMessages: noErrors,
      })
      expect(state.showStandaloneRerun).toBe(false)
    })
  })

  describe('close button (Active health, no agent running)', () => {
    it('shows the close button when health is Active and no agent runs on this issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Active }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showClose).toBe(true)
    })

    it('hides the close button when an agent is running on this issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ number: 14, status: IssueStatus.InProgress, health: IssueHealth.Active }),
        agentStatus: makeAgentStatus({
          activeAgents: [{ issueId: 'i-14', issueNumber: 14, projectId: 'proj-1' }],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showClose).toBe(false)
    })

    it('hides the close button when health is not Active', () => {
      const state = computeActionsState({
        issue: makeIssue({ status: IssueStatus.InProgress, health: IssueHealth.Blocked }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showClose).toBe(false)
    })
  })

  describe('error surface', () => {
    it('shows no error when all error messages are absent', () => {
      const state = computeActionsState({
        issue: makeIssue(),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showError).toBe(false)
    })

    it('shows the error surface when at least one error is present', () => {
      const state = computeActionsState({
        issue: makeIssue(),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: { ...noErrors, retryError: new Error('failed') },
      })
      expect(state.showError).toBe(true)
    })
  })

  describe('other-agents indicator', () => {
    it('shows the indicator when agents are running on other issues and this issue is not backlog', () => {
      const state = computeActionsState({
        issue: makeIssue({ number: 1, status: IssueStatus.InProgress }),
        agentStatus: makeAgentStatus({
          activeAgents: [{ issueId: 'i-2', issueNumber: 2, projectId: 'proj-1' }],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showOtherAgents).toBe(true)
      expect(state.otherAgentsCount).toBe(1)
    })

    it('hides the indicator when no agents are active', () => {
      const state = computeActionsState({
        issue: makeIssue({ number: 1, status: IssueStatus.InProgress }),
        agentStatus: makeAgentStatus({ activeAgents: [] }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showOtherAgents).toBe(false)
      expect(state.otherAgentsCount).toBe(0)
    })

    it('hides the indicator when an agent runs on this issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ number: 1, status: IssueStatus.InProgress }),
        agentStatus: makeAgentStatus({
          activeAgents: [{ issueId: 'i-1', issueNumber: 1, projectId: 'proj-1' }],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showOtherAgents).toBe(false)
    })

    it('hides the indicator for a backlog issue', () => {
      const state = computeActionsState({
        issue: makeIssue({ number: 1, status: IssueStatus.Backlog }),
        agentStatus: makeAgentStatus({
          activeAgents: [{ issueId: 'i-2', issueNumber: 2, projectId: 'proj-1' }],
        }),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.showOtherAgents).toBe(false)
    })
  })

  describe('purity / no React/DOM dependencies', () => {
    it('is callable with plain data (no React/DOM objects)', () => {
      const state = computeActionsState({
        issue: makeIssue(),
        agentStatus: undefined,
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state).toBeDefined()
    })

    it('accepts undefined agentStatus / workflowTimeline / recovery without throwing', () => {
      expect(() => computeActionsState({
        issue: makeIssue({ recovery: undefined }),
        agentStatus: undefined,
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })).not.toThrow()
    })

    it('uses RecoveryProjection.latestAttemptState to derive isInterrupted', () => {
      const recovery: RecoveryProjection = {
        currentWorkItem: { type: 'task', id: 't-1', title: 'A task' },
        latestAttemptState: 'interrupted',
        workflowSummaryState: 'waiting-for-recovery',
        allowedActions: [],
      }
      const state = computeActionsState({
        issue: makeIssue({
          status: IssueStatus.InProgress,
          health: IssueHealth.Interrupted,
          recovery,
        }),
        agentStatus: makeAgentStatus(),
        workflowTimeline: undefined,
        errorMessages: noErrors,
      })
      expect(state.blockedActions.isInterrupted).toBe(true)
      expect(state.blockedActions.currentWorkItem).toEqual({ type: 'task', id: 't-1', title: 'A task' })
    })
  })
})
