import { describe, expect, it } from 'vitest'
import {
  deriveRuntimeDecision,
  type RuntimeDecisionInput,
} from './derive-runtime-decision'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'

function baseIssue(overrides: Partial<RuntimeDecisionInput['issue']> = {}): RuntimeDecisionInput['issue'] {
  return {
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Build,
    workflowStatus: 'running',
    health: IssueHealth.Active,
    approvalState: undefined,
    blockedReason: undefined,
    recovery: undefined,
    convergence: undefined,
    drift: undefined,
    workflowStageProgress: undefined,
    prerequisites: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

describe('deriveRuntimeDecision', () => {
  it('returns running when an active agent is on the issue and no failing checks are present', () => {
    const input: RuntimeDecisionInput = {
      issue: baseIssue({
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Implement action controls' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'inspect'],
        },
      }),
      timeline: {
        currentStage: WorkflowStage.Build,
        status: 'Running',
        stages: [
          {
            stage: WorkflowStage.Build,
            status: 'running',
            order: 2,
            startedAt: null,
            completedAt: null,
            durationMs: null,
            tasks: [
              {
                id: 't1',
                title: 'Implement action controls',
                uses: null,
                status: 'running',
                startedAt: null,
                completedAt: null,
                durationMs: null,
                attempts: 1,
                message: null,
              },
            ],
            checks: [],
            approval: null,
          },
        ],
        pendingWork: null,
        availableActions: [{ name: 'stop', label: 'Stop', target: null }],
      },
      hasActiveAgent: true,
    }

    const decision = deriveRuntimeDecision(input)

    expect(decision.summary).toBe('running')
    expect(decision.currentTask?.title).toBe('Implement action controls')
    expect(decision.headline).toContain('Implement action controls')
    expect(decision.actions.some((a) => a.kind === 'stop' && a.enabled)).toBe(true)
    expect(decision.primary?.kind).toBe('stop')
    expect(decision.primary?.label).toBe('Stop')
    expect(decision.stopRecoverable).toBe(true)
  })

  it('returns done when the workflow stage is Done', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        workflowStage: WorkflowStage.Done,
        workflowStatus: 'passed',
        health: IssueHealth.Done,
        status: IssueStatus.Done,
      }),
    })

    expect(decision.summary).toBe('done')
  })

  it('returns approval-required when approvalState.status is awaiting and no failed checks block it', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        workflowStage: WorkflowStage.Plan,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Plan,
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
        recovery: {
          currentWorkItem: null,
          latestAttemptState: null,
          workflowSummaryState: 'awaiting-approval',
          allowedActions: ['approve', 'reject'],
        },
      }),
      timeline: {
        currentStage: WorkflowStage.Plan,
        status: 'AwaitingApproval',
        stages: [
          {
            stage: WorkflowStage.Plan,
            status: 'awaiting-approval',
            order: 1,
            startedAt: null,
            completedAt: null,
            durationMs: null,
            tasks: [],
            checks: [],
            approval: null,
          },
        ],
        pendingWork: null,
        availableActions: [
          { name: 'approve', label: 'Approve', target: null },
          { name: 'reject', label: 'Send back', target: null },
        ],
      },
    })

    expect(decision.summary).toBe('approval-required')
    expect(decision.actions.find((a) => a.kind === 'approve')?.enabled).toBe(true)
    expect(decision.actions.find((a) => a.kind === 'send-back')?.enabled).toBe(true)
    expect(decision.primary?.kind).toBe('approve')
    expect(decision.stopRecoverable).toBeNull()
  })

  it('returns failed (not approval-required) when a Check stage has a failed script/health verification even when approval is awaiting', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        workflowStage: WorkflowStage.Check,
        approvalState: {
          status: 'awaiting',
          stage: WorkflowStage.Check,
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'failed',
          workflowSummaryState: 'waiting-for-recovery',
          allowedActions: ['retry', 'rerun'],
        },
      }),
      timeline: {
        currentStage: WorkflowStage.Check,
        status: 'Failed',
        stages: [
          {
            stage: WorkflowStage.Check,
            status: 'failed',
            order: 3,
            startedAt: null,
            completedAt: null,
            durationMs: null,
            tasks: [],
            checks: [
              {
                name: 'health',
                title: 'Health check',
                uses: null,
                status: 'failed',
                message: 'Typecheck failed',
                startedAt: null,
                completedAt: null,
                durationMs: null,
              },
            ],
            approval: null,
          },
        ],
        pendingWork: null,
        availableActions: [
          { name: 'retry', label: 'Retry', target: null },
          { name: 'rerun', label: 'Rerun', target: null },
        ],
      },
    })

    expect(decision.summary).toBe('failed')
    expect(decision.actions.find((a) => a.kind === 'approve')?.enabled).toBeFalsy()
  })

  it('returns queued when an explicit queue/wait signal is present (prerequisite waiting)', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.Backlog,
        workflowStage: null,
        health: IssueHealth.Active,
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 98, title: 'Prereq issue' } },
      }),
    })

    expect(decision.summary).toBe('queued')
    expect(decision.waitReason).toContain('#98')
  })

  it('returns queued when the runner is unavailable', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.Backlog,
        workflowStage: null,
        health: IssueHealth.Active,
      }),
      agentStatus: {
        runnerAvailable: false,
        runnerMessage: 'Runner offline',
        capacity: { active: 0, max: 1 },
        activeAgents: [],
      },
    })

    expect(decision.summary).toBe('queued')
    expect(decision.primary?.kind).toBe('start')
    expect(decision.primary?.enabled).toBe(false)
    expect(decision.primary?.reason).toBe('Runner offline')
    expect(decision.waitReason).toBe('Runner offline')
  })

  it('returns queued with Start as the primary action for a ready backlog issue', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.Backlog,
        workflowStage: null,
        workflowStatus: null,
        health: IssueHealth.Active,
        canStart: true,
        blocker: null,
      }),
      agentStatus: {
        runnerAvailable: true,
        capacity: { active: 0, max: 2 },
        activeAgents: [],
      },
    })

    expect(decision.summary).toBe('queued')
    expect(decision.primary?.kind).toBe('start')
    expect(decision.primary?.enabled).toBe(true)
    expect(decision.actions.some((a) => a.kind === 'stop')).toBe(false)
    expect(decision.nextAction).toBe('Start the workflow.')
  })

  it('falls back to running (not queued) when no explicit queue/wait signal is present', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        status: IssueStatus.InProgress,
        workflowStage: WorkflowStage.Build,
        health: IssueHealth.Active,
      }),
      agentStatus: {
        runnerAvailable: true,
        capacity: { active: 0, max: 4 },
        activeAgents: [],
      },
    })

    expect(decision.summary).toBe('running')
  })

  it('returns blocked when health is Blocked', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        health: IssueHealth.Blocked,
        blockedReason: 'Convergence blocked on unmerged base',
        workflowStage: WorkflowStage.Integrate,
      }),
    })

    expect(decision.summary).toBe('blocked')
    expect(decision.blockedReason).toContain('Convergence blocked')
  })

  it('returns blocked when convergence has unresolved items', () => {
    const decision = deriveRuntimeDecision({
      issue: baseIssue({
        health: IssueHealth.Active,
        convergence: {
          unresolvedItemIds: ['check-1'],
          blockingItemCount: 1,
          directlyRepairedCount: 0,
          reactionAttempts: 0,
          attemptedItemIds: [],
          resolvedItemIds: [],
          newBlockingItemIds: [],
          nonBlockingItemIds: [],
        },
      }),
    })

    expect(decision.summary).toBe('blocked')
  })

  describe('current task naming fallbacks', () => {
    it('uses recovery.currentWorkItem.title first', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          recovery: {
            currentWorkItem: { type: 'task', id: 't1', title: 'Recovery work item' },
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: [],
          },
          workflowStageProgress: {
            stage: WorkflowStage.Build,
            total: 1,
            completed: 0,
            running: 1,
            failed: 0,
            currentTaskTitle: 'Different stage progress title',
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Running',
          stages: [],
          pendingWork: null,
          availableActions: [],
        },
      })

      expect(decision.currentTask?.title).toBe('Recovery work item')
      expect(decision.currentTask?.kind).toBe('task')
    })

    it('falls back to workflowStageProgress.currentTaskTitle', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: [],
          },
          workflowStageProgress: {
            stage: WorkflowStage.Build,
            total: 1,
            completed: 0,
            running: 1,
            failed: 0,
            currentTaskTitle: 'Stage progress title',
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Running',
          stages: [],
          pendingWork: null,
          availableActions: [],
        },
      })

      expect(decision.currentTask?.title).toBe('Stage progress title')
    })

    it('falls back to the first running task in the timeline', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'running',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Running',
          stages: [
            {
              stage: WorkflowStage.Build,
              status: 'running',
              order: 2,
              startedAt: null,
              completedAt: null,
              durationMs: null,
              tasks: [
                {
                  id: 't1',
                  title: 'First running task',
                  uses: null,
                  status: 'running',
                  startedAt: null,
                  completedAt: null,
                  durationMs: null,
                  attempts: 1,
                  message: null,
                },
              ],
              checks: [],
              approval: null,
            },
          ],
          pendingWork: null,
          availableActions: [],
        },
      })

      expect(decision.currentTask?.title).toBe('First running task')
    })

    it('falls back to the first running check in the timeline', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'running',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Check,
          status: 'Running',
          stages: [
            {
              stage: WorkflowStage.Check,
              status: 'running',
              order: 3,
              startedAt: null,
              completedAt: null,
              durationMs: null,
              tasks: [],
              checks: [
                {
                  name: 'health',
                  title: 'Typecheck',
                  uses: null,
                  status: 'running',
                  message: null,
                  startedAt: null,
                  completedAt: null,
                  durationMs: null,
                },
              ],
              approval: null,
            },
          ],
          pendingWork: null,
          availableActions: [],
        },
      })

      expect(decision.currentTask?.kind).toBe('check')
      expect(decision.currentTask?.title).toBe('Typecheck')
    })

    it('returns null currentTask when there is no recoverable work item signal', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue(),
        timeline: {
          currentStage: null,
          status: 'Idle',
          stages: [],
          pendingWork: null,
          availableActions: [],
        },
      })

      expect(decision.currentTask).toBeNull()
    })
  })

  describe('action availability from projections', () => {
    it('enables retry/resume/rerun only when recovery or timeline exposes the action', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: ['resume'],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Failed',
          stages: [],
          pendingWork: null,
          availableActions: [{ name: 'resume', label: 'Resume', target: null }],
        },
      })

      expect(decision.summary).toBe('failed')
      expect(decision.actions.find((a) => a.kind === 'resume')?.enabled).toBe(true)
      expect(decision.primary?.kind).toBe('resume')
      expect(decision.actions.find((a) => a.kind === 'retry')?.enabled).toBe(false)
      expect(decision.actions.find((a) => a.kind === 'rerun')?.enabled).toBe(false)
      expect(decision.actions.find((a) => a.kind === 'start')?.enabled).toBe(false)
    })

    it('enables an action when it comes only from workflowTimeline.availableActions', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Active,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Running',
          stages: [],
          pendingWork: null,
          availableActions: [{ name: 'stop', label: 'Stop', target: null }],
        },
      })

      expect(decision.actions.find((a) => a.kind === 'stop')?.enabled).toBe(true)
    })

    it('disables all actions when no projections expose any action (for an approval-required state)', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          workflowStage: WorkflowStage.Plan,
          approvalState: {
            status: 'awaiting',
            stage: WorkflowStage.Plan,
            requestedAt: '2026-01-01T00:00:00.000Z',
          },
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'awaiting-approval',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Plan,
          status: 'AwaitingApproval',
          stages: [],
          pendingWork: null,
          availableActions: [],
        },
      })

      expect(decision.summary).toBe('approval-required')
      expect(decision.actions.find((a) => a.kind === 'approve')?.enabled).toBe(false)
      expect(decision.actions.find((a) => a.kind === 'send-back')?.enabled).toBe(false)
      expect(decision.actions.find((a) => a.kind === 'approve')?.reason).toBeTruthy()
    })

    it('does not infer blocked recovery actions without a projection', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          status: IssueStatus.InProgress,
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          blockedReason: 'Something blocked us',
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Failed',
          stages: [],
          pendingWork: null,
          availableActions: [],
        },
      })

      const actionKinds = decision.actions.map((a) => a.kind)
      for (const kind of ['retry', 'resume', 'rerun', 'start'] as const) {
        const found = decision.actions.find((a) => a.kind === kind)
        expect(found, `expected an action entry for ${kind}`).toBeDefined()
        expect(found?.enabled, `expected ${kind} to be disabled when no projection allows it`).toBe(false)
      }
      expect(actionKinds).not.toContain('approve')
      expect(actionKinds).not.toContain('send-back')
      expect(actionKinds).not.toContain('stop')
    })

    it('exposes Start new workflow (not Stop) when a failed workflow offers start', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          workflowStage: WorkflowStage.Build,
          recovery: {
            currentWorkItem: { type: 'task', id: 't1', title: 'Broken task' },
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Failed',
          stages: [],
          pendingWork: null,
          availableActions: [{ name: 'start', label: 'Start new workflow', target: null }],
        },
      })

      expect(decision.summary).toBe('failed')
      expect(decision.actions.find((a) => a.kind === 'start')?.enabled).toBe(true)
      expect(decision.actions.some((a) => a.kind === 'stop')).toBe(false)
      expect(decision.primary?.kind).toBe('start')
      expect(decision.stopRecoverable).toBeNull()
      expect(decision.nextAction).toBe('Start a new workflow run, discarding the failed one.')
    })

    it('marks stop as terminal when only the workflow timeline exposes stop even with an active agent', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Active,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: null,
            workflowSummaryState: 'running',
            allowedActions: [],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Running',
          stages: [],
          pendingWork: null,
          availableActions: [{ name: 'stop', label: 'Stop', target: null }],
        },
        hasActiveAgent: true,
      })

      expect(decision.primary?.kind).toBe('stop')
      expect(decision.primary?.label).toBe('Stop')
      expect(decision.stopRecoverable).toBe(false)
    })

    it('chooses the first enabled non-inspect action as the single primary action', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          workflowStage: WorkflowStage.Build,
          health: IssueHealth.Blocked,
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: ['rerun'],
          },
        }),
        timeline: {
          currentStage: WorkflowStage.Build,
          status: 'Failed',
          stages: [],
          pendingWork: null,
          availableActions: [{ name: 'rerun', label: 'Rerun', target: null }],
        },
      })

      expect(decision.actions.find((a) => a.kind === 'retry')?.enabled).toBe(false)
      expect(decision.primary?.kind).toBe('rerun')
    })
  })

  describe('drift and secondary notes', () => {
    it('surfaces a drift note when base drift requires attention', () => {
      const decision = deriveRuntimeDecision({
        issue: baseIssue({
          drift: {
            drifted: true,
            decision: 'needs-attention',
            safeWindow: null,
            deferReason: null,
            observedBaseSha: null,
            currentBaseSha: null,
            candidateHeadSha: null,
            mergeBaseSha: null,
            conflicts: null,
            nextAction: 'Rebase manually before resuming.',
          },
        }),
      })

      expect(decision.driftNote).toBe('Rebase manually before resuming.')
    })
  })
})
