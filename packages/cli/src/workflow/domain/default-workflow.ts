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
        { id: 'proposal', title: 'Generate proposal' },
        { id: 'specs', title: 'Write specs' },
        { id: 'design', title: 'Create design' },
        { id: 'tasks', title: 'Generate tasks' },
        { id: 'self-review', title: 'Self review', resultContract: SELF_REVIEW_RESULT_CONTRACT },
      ],
      checks: [
        { name: 'proposal-complete', title: 'Proposal complete' },
        { name: 'specs-complete', title: 'Specs complete' },
        { name: 'design-complete', title: 'Design complete' },
        { name: 'tasks-valid', title: 'Tasks valid' },
        { name: 'self-review-passed', title: 'Self review passed' },
        { name: 'health:plan', title: 'Plan health gate' },
      ],
      requiresApproval: true,
      approvalCheckName: 'user-approval',
      checkFailurePolicies: [
        {
          checkName: 'self-review-passed',
          fixTaskId: 'fix-plan-review',
          fixTaskTitle: 'Fix plan review findings',
          maxAttempts: 1,
        },
      ],
      workSources: [
        { kind: 'static', taskIds: ['proposal', 'specs', 'design', 'tasks', 'self-review'] },
      ],
      taskExecutionPolicies: [
        { taskId: 'proposal', kind: 'agent-session', agentSessionRef: 'plan-artifacts' },
        { taskId: 'specs', kind: 'agent-session', agentSessionRef: 'plan-artifacts' },
        { taskId: 'design', kind: 'agent-session', agentSessionRef: 'plan-artifacts' },
        { taskId: 'tasks', kind: 'agent-session', agentSessionRef: 'plan-artifacts' },
        { taskId: 'self-review', kind: 'agent-session', agentSessionRef: 'plan-artifacts' },
        { taskId: 'fix-plan-review', kind: 'repair-task', workSourceKind: 'runtime' },
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
      repairPolicies: [
        {
          checkName: 'self-review-passed',
          fixTaskId: 'fix-plan-review',
          fixTaskTitle: 'Fix plan review findings',
          maxAttempts: 1,
        },
      ],
      invalidationPolicy: {
        entries: [],
      },
    },
    {
      stage: Stage.Build,
      tasks: [],
      checks: [
        { name: 'health:build', title: 'Build health gate' },
      ],
      checkFailurePolicies: [
        {
          checkName: 'health:build',
          fixTaskId: 'fix-build-health',
          fixTaskTitle: 'Fix build health',
          maxAttempts: 1,
        },
      ],
      workSources: [
        { kind: 'ralph' },
        { kind: 'runtime' },
      ],
      taskExecutionPolicies: [
        { taskId: 'fix-build-health', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
        { taskId: '*', kind: 'ralph-task', workSourceKind: 'ralph' },
      ],
      checkPolicies: [
        { checkName: 'health:build', phase: 'post-task' },
      ],
      repairPolicies: [
        {
          checkName: 'health:build',
          fixTaskId: 'fix-build-health',
          fixTaskTitle: 'Fix build health',
          maxAttempts: 1,
        },
      ],
      invalidationPolicy: {
        entries: [],
      },
    },
    {
      stage: Stage.Check,
      tasks: [
        {
          id: 'ai-review',
          title: 'AI review',
          resultContract: REVIEW_RESULT_CONTRACT,
          selfRepairPolicy: REVIEW_SELF_REPAIR_POLICY,
        },
      ],
      checks: [
        { name: 'health:check', title: 'Check health gate' },
        { name: 'review-passed', title: 'Review passed' },
        { name: 'merge-ready', title: 'Merge ready' },
      ],
      requiresApproval: true,
      approvalCheckName: 'user-approval',
      checkFailurePolicies: [
        {
          checkName: 'health:check',
          fixTaskId: 'fix-check-health',
          fixTaskTitle: 'Fix check health',
          maxAttempts: 1,
        },
        {
          checkName: 'review-passed',
          fixTaskId: 'fix-review-findings',
          fixTaskTitle: 'Fix review findings',
          maxAttempts: 1,
          inputFrom: [
            { type: 'failed-check-output' },
            { type: 'check-items', filter: 'blocking' },
            { type: 'snapshot' },
          ],
        },
        {
          checkName: 'merge-ready',
          fixTaskId: 'fix-merge-readiness',
          fixTaskTitle: 'Fix merge readiness',
          maxAttempts: 1,
        },
      ],
      workSources: [
        { kind: 'static', taskIds: ['ai-review'] },
        { kind: 'runtime' },
      ],
      taskExecutionPolicies: [
        { taskId: 'ai-review', kind: 'agent-session' },
        { taskId: 'fix-check-health', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'fix-review-findings', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'fix-merge-readiness', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'check:converge-review-snapshot', kind: 'service-call', workSourceKind: 'runtime' },
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
      ],
      checkPolicies: [
        { checkName: 'health:check', phase: 'post-task' },
        { checkName: 'review-passed', phase: 'post-task' },
        { checkName: 'merge-ready', phase: 'post-task' },
      ],
      approvalPolicy: { checkName: 'user-approval' },
      repairPolicies: [
        {
          checkName: 'health:check',
          fixTaskId: 'fix-check-health',
          fixTaskTitle: 'Fix check health',
          maxAttempts: 1,
        },
        {
          checkName: 'review-passed',
          fixTaskId: 'fix-review-findings',
          fixTaskTitle: 'Fix review findings',
          maxAttempts: 1,
          inputFrom: [
            { type: 'failed-check-output' },
            { type: 'check-items', filter: 'blocking' },
            { type: 'snapshot' },
          ],
        },
        {
          checkName: 'merge-ready',
          fixTaskId: 'fix-merge-readiness',
          fixTaskTitle: 'Fix merge readiness',
          maxAttempts: 1,
        },
      ],
      invalidationPolicy: {
        entries: [
          {
            trigger: 'task-completion',
            triggerTaskId: 'fix-review-findings',
            reason: 'Review findings changed code; re-run AI review before rechecking',
            invalidates: {
              tasks: ['ai-review'],
              checks: ['health:check', 'review-passed', 'merge-ready'],
              approval: true,
            },
          },
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
        { id: 'integrate:spec-sync', title: 'Sync specs' },
        { id: 'integrate:archive-change', title: 'Archive change' },
        { id: 'integrate:merge', title: 'Merge branch' },
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
        { taskId: 'integrate:spec-sync', kind: 'service-call' },
        { taskId: 'integrate:archive-change', kind: 'service-call' },
        { taskId: 'integrate:merge', kind: 'service-call' },
        { taskId: 'fix-integrate-health', kind: 'repair-task', workSourceKind: 'runtime' },
        { taskId: 'rebase-branch', kind: 'rebase-task', workSourceKind: 'runtime' },
      ],
      checkPolicies: [
        { checkName: 'health:integrate', phase: 'post-task' },
      ],
      evidenceRequirements: [
        { taskId: 'integrate:spec-sync', uses: 'mohist/openspec-sync' },
        { taskId: 'integrate:archive-change', uses: 'mohist/archive-change' },
        { taskId: 'integrate:merge', uses: 'mohist/merge' },
        { checkName: 'health:integrate', uses: 'mohist/health-gate' },
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
