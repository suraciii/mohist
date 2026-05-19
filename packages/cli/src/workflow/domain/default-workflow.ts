import { Stage } from '../../types';
import { REVIEW_RESULT_CONTRACT, REVIEW_SELF_REPAIR_POLICY, SELF_REVIEW_RESULT_CONTRACT } from './contracts';
import type { StageDefinition, WorkflowDefinition, WorkflowDefinitionSnapshot } from './types';
import { compileWorkflowDefinition, createWorkflowDefinitionSnapshot } from './workflow-definition';

export const MOHIST_DEFAULT_WORKFLOW_DEFINITION: WorkflowDefinition = {
  id: 'mohist/default',
  name: 'Mohist default issue delivery workflow',
  stages: [
    {
      stage: Stage.Plan,
      tasks: [
        { id: 'proposal', title: 'Generate proposal', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/proposal' } } },
        { id: 'specs', title: 'Write specs', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/specs' } } },
        { id: 'design', title: 'Create design', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/design' } } },
        { id: 'tasks', title: 'Generate tasks', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/tasks' } } },
        { id: 'self-review', title: 'Self review', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/self-review' } }, resultContract: SELF_REVIEW_RESULT_CONTRACT },
      ],
      checks: [
        { name: 'proposal-complete', title: 'Proposal complete' },
        { name: 'specs-complete', title: 'Specs complete' },
        { name: 'design-complete', title: 'Design complete' },
        { name: 'tasks-valid', title: 'Tasks valid' },
        {
          name: 'self-review-passed',
          title: 'Self review passed',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-plan-review',
                title: 'Fix plan review findings',
                uses: 'mohist/agent',
                with: { prompt: { ref: 'mohist/plan/fix-review' } },
              },
            },
          },
        },
        { name: 'health:plan', title: 'Plan health gate' },
      ],
      requiresApproval: true,
      approvalCheckName: 'user-approval',
      workSources: [
        { kind: 'static', taskIds: ['proposal', 'specs', 'design', 'tasks', 'self-review'] },
      ],
      taskExecutionPolicies: [
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
      ],
      checkPolicies: [
        { checkName: 'proposal-complete', phase: 'post-task' },
        { checkName: 'specs-complete', phase: 'post-task' },
        { checkName: 'design-complete', phase: 'post-task' },
        { checkName: 'tasks-valid', phase: 'post-task' },
        { checkName: 'self-review-passed', phase: 'post-task' },
        { checkName: 'health:plan', phase: 'post-task' },
      ],
      approvalPolicy: { checkName: 'user-approval' },
      invalidationPolicy: {
        entries: [],
      },
    },
    {
      stage: Stage.Build,
      tasks: [],
      checks: [
        {
          name: 'health:build',
          title: 'Build health gate',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-build-health',
                title: 'Fix build health',
                uses: 'mohist/agent',
                with: { prompt: { ref: 'mohist/build/fix-health' } },
              },
            },
          },
        },
      ],
      workSources: [
        { kind: 'ralph' },
        { kind: 'runtime' },
      ],
      taskExecutionPolicies: [
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
        { taskId: '*', kind: 'ralph-task', workSourceKind: 'ralph' },
      ],
      checkPolicies: [
        { checkName: 'health:build', phase: 'post-task' },
      ],
      invalidationPolicy: {
        entries: [],
      },
    },
    {
      stage: Stage.Check,
      on: {
        'code.changed': { reset: 'checks-and-approval' },
      },
      tasks: [
        {
          id: 'ai-review',
          title: 'AI review',
          uses: 'mohist/agent',
          with: { prompt: { ref: 'mohist/check/ai-review' } },
          resultContract: REVIEW_RESULT_CONTRACT,
          selfRepairPolicy: REVIEW_SELF_REPAIR_POLICY,
        },
      ],
      checks: [
        {
          name: 'health:check',
          title: 'Check health gate',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-check-health',
                title: 'Fix check health',
                uses: 'mohist/agent',
                emits: ['code.changed'],
                with: { prompt: { ref: 'mohist/check/fix-health' } },
              },
            },
          },
        },
        {
          name: 'review-passed',
          title: 'Review passed',
          onFailure: {
            retry: {
              limit: 2,
              task: {
                id: 'fix-review-findings',
                title: 'Fix review findings',
                uses: 'mohist/agent',
                emits: ['code.changed'],
                with: {
                  prompt: {
                    inline: [
                      'Fix the blocking findings in:',
                      '',
                      '{{ openspec.changeDir }}/review.md',
                      '',
                      'Apply the minimal code changes required.',
                      'Do not edit review.md.',
                    ].join('\n'),
                  },
                },
              },
            },
          },
        },
        {
          name: 'merge-ready',
          title: 'Merge ready',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-merge-readiness',
                title: 'Fix merge readiness',
                uses: 'mohist/rebase',
                emits: ['code.changed'],
              },
            },
          },
        },
      ],
      requiresApproval: true,
      approvalCheckName: 'user-approval',
      workSources: [
        { kind: 'static', taskIds: ['ai-review'] },
        { kind: 'runtime' },
      ],
      taskExecutionPolicies: [
        { taskId: 'check:converge-review-snapshot', kind: 'service-call', workSourceKind: 'runtime' },
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
      ],
      checkPolicies: [
        { checkName: 'health:check', phase: 'post-task' },
        { checkName: 'review-passed', phase: 'post-task' },
        { checkName: 'merge-ready', phase: 'post-task' },
      ],
      approvalPolicy: { checkName: 'user-approval' },
      invalidationPolicy: {
        entries: [
          {
            trigger: 'task-completion',
            triggerTaskId: 'rebase-branch',
            when: { shaChanged: true },
            reason: 'Rebase changed the candidate snapshot; re-run review checks',
            invalidates: {
              tasks: ['ai-review'],
              checks: ['health:check', 'review-passed', 'merge-ready'],
              approval: true,
            },
          },
        ],
      },
    },
    {
      stage: Stage.Integrate,
      tasks: [
        { id: 'integrate:spec-sync', title: 'Sync specs', uses: 'mohist/openspec-sync' },
        { id: 'integrate:archive-change', title: 'Archive change', uses: 'mohist/archive-change' },
        { id: 'integrate:merge', title: 'Merge branch', uses: 'mohist/merge' },
      ],
      checks: [
        { name: 'health:integrate', title: 'Post-merge health check' },
      ],
      checkFailurePolicies: [
        {
          checkName: 'health:integrate',
          fixTaskId: 'fix-integrate-health',
          fixTaskTitle: 'Fix integrate health',
          maxAttempts: 1,
        },
      ],
      workSources: [
        { kind: 'static', taskIds: ['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge'] },
      ],
      taskExecutionPolicies: [
        { taskId: 'fix-integrate-health', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
      ],
      checkPolicies: [
        { checkName: 'health:integrate', phase: 'post-task' },
      ],
      repairPolicies: [],
      invalidationPolicy: {
        entries: [],
      },
    },
  ],
};

export const DEFAULT_STAGE_DEFINITIONS: StageDefinition[] = compileWorkflowDefinition(MOHIST_DEFAULT_WORKFLOW_DEFINITION);

export function createDefaultWorkflowDefinitionSnapshot(capturedAt?: string): WorkflowDefinitionSnapshot {
  return createWorkflowDefinitionSnapshot({
    definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
    source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
    capturedAt,
  });
}
