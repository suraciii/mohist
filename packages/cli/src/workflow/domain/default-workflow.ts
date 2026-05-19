import { Stage } from '../../types';
import { REVIEW_RESULT_CONTRACT, REVIEW_SELF_REPAIR_POLICY, SELF_REVIEW_RESULT_CONTRACT } from './contracts';
import type { CompiledStageDefinition, WorkflowDefinition, WorkflowDefinitionSnapshot } from './types';
import { parseWorkflowDefinitionSource, type WorkflowSourceDefinition } from './workflow-definition-parser';
import { compileWorkflowDefinition, createWorkflowDefinitionSnapshot } from './workflow-definition';

const DEFAULT_PLAN_HEALTH_COMMAND = 'npm ci && npm run typecheck';
const DEFAULT_BUILD_HEALTH_COMMAND = 'npm ci && npm run build';
const DEFAULT_CHECK_HEALTH_COMMAND = 'npm ci && npm run build && npm test';
const DEFAULT_HEALTH_TIMEOUT_MS = 5 * 60 * 1000;

export const MOHIST_DEFAULT_WORKFLOW_SOURCE: WorkflowSourceDefinition = {
  id: 'mohist/default',
  name: 'Mohist default issue delivery workflow',
  stages: [
    {
      id: Stage.Plan,
      tasks: [
        { id: 'proposal', title: 'Generate proposal', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/proposal' } } },
        { id: 'specs', title: 'Write specs', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/specs' } } },
        { id: 'design', title: 'Create design', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/design' } } },
        { id: 'tasks', title: 'Generate tasks', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/tasks' } } },
        { id: 'self-review', title: 'Self review', uses: 'mohist/agent', with: { session: 'plan-artifacts', prompt: { ref: 'mohist/plan/self-review' } }, resultContract: SELF_REVIEW_RESULT_CONTRACT },
      ],
      checks: [
        { id: 'proposal-complete', title: 'Proposal complete' },
        { id: 'specs-complete', title: 'Specs complete' },
        { id: 'design-complete', title: 'Design complete' },
        { id: 'tasks-valid', title: 'Tasks valid' },
        {
          id: 'self-review-passed',
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
        {
          id: 'health:plan',
          title: 'Plan health gate',
          uses: 'mohist/health-gate',
          with: { command: DEFAULT_PLAN_HEALTH_COMMAND, timeout: DEFAULT_HEALTH_TIMEOUT_MS },
        },
      ],
      approval: true,
    },
    {
      id: Stage.Build,
      tasksFrom: 'mohist/ralph-tasks',
      checks: [
        {
          id: 'health:build',
          title: 'Build health gate',
          uses: 'mohist/health-gate',
          with: { command: DEFAULT_BUILD_HEALTH_COMMAND, timeout: DEFAULT_HEALTH_TIMEOUT_MS },
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
    },
    {
      id: Stage.Check,
      on: {
        'code.changed': {
          reset: 'checks-and-approval',
          tasks: ['ai-review'],
          checks: 'all',
          approval: true,
        },
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
          id: 'health:check',
          title: 'Check health gate',
          uses: 'mohist/health-gate',
          with: {
            command: DEFAULT_CHECK_HEALTH_COMMAND,
            timeout: DEFAULT_HEALTH_TIMEOUT_MS,
            approvalEvidence: { role: 'verification' },
          },
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
          id: 'review-passed',
          title: 'Review passed',
          uses: 'mohist/verdict',
          with: {
            approvalEvidence: {
              role: 'verdict',
              snapshotField: 'snapshotSha',
            },
          },
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
          id: 'merge-ready',
          title: 'Merge ready',
          uses: 'mohist/merge-ready',
          with: {
            approvalEvidence: {
              role: 'candidate',
              snapshotField: 'candidateHeadSha',
            },
          },
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
      approval: true,
    },
    {
      id: Stage.Integrate,
      tasks: [
        { id: 'integrate:spec-sync', title: 'Sync specs', uses: 'mohist/openspec-sync' },
        { id: 'integrate:archive-change', title: 'Archive change', uses: 'mohist/archive-change' },
        { id: 'integrate:merge', title: 'Merge branch', uses: 'mohist/merge' },
      ],
      checks: [
        {
          id: 'health:integrate',
          title: 'Post-merge health check',
          uses: 'mohist/health-gate',
          with: { command: DEFAULT_CHECK_HEALTH_COMMAND, timeout: DEFAULT_HEALTH_TIMEOUT_MS },
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-integrate-health',
                title: 'Fix integrate health',
                uses: 'mohist/agent',
                with: { prompt: { ref: 'mohist/integrate/fix-health' } },
              },
            },
          },
        },
      ],
    },
  ],
};

export const MOHIST_DEFAULT_WORKFLOW_DEFINITION: WorkflowDefinition = parseWorkflowDefinitionSource(
  MOHIST_DEFAULT_WORKFLOW_SOURCE,
  { taskSource: 'builtin', checkSource: 'builtin' },
);

export const DEFAULT_STAGE_DEFINITIONS: CompiledStageDefinition[] = compileWorkflowDefinition(MOHIST_DEFAULT_WORKFLOW_DEFINITION);

export function createDefaultWorkflowDefinitionSnapshot(capturedAt?: string): WorkflowDefinitionSnapshot {
  return createWorkflowDefinitionSnapshot({
    definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
    source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
    capturedAt,
  });
}
