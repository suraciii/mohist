import type { WorkflowDefinition, StageDefinition } from '@mohist/workflow';
import { Stage } from '../../types';

const DEFAULT_PLAN_HEALTH_COMMAND = 'npm ci && npm run typecheck';
const DEFAULT_BUILD_HEALTH_COMMAND = 'npm ci && npm run build';
const DEFAULT_CHECK_HEALTH_COMMAND = 'npm ci && npm run build && npm test';
const DEFAULT_HEALTH_TIMEOUT_MS = 5 * 60 * 1000;

export const MOHIST_DEFAULT_WORKFLOW_DEFINITION: WorkflowDefinition = {
  id: 'mohist/default',
  name: 'Mohist default issue delivery workflow',
  artifacts: {
    openspecChange: '{{ openspec.changeDir }}',
  },
  stages: [
    {
      stage: Stage.Plan,
      tasks: [
        {
          id: 'proposal',
          title: 'Generate proposal',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/proposal.md'],
            prompt: { inline: 'Create the proposal artifact for issue #{{ issue.number }}: {{ issue.title }}.\n\nWrite the result to: {{ artifacts.openspecChange }}/proposal.md\n\nUse the existing change artifacts in {{ artifacts.openspecChange }} as context when relevant.' },
          },
        },
        {
          id: 'specs',
          title: 'Write specs',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/specs'],
            prompt: { inline: 'Create the specs artifact for issue #{{ issue.number }}: {{ issue.title }}.\n\nWrite the result to: {{ artifacts.openspecChange }}/specs\n\nUse the existing change artifacts in {{ artifacts.openspecChange }} as context when relevant.' },
          },
        },
        {
          id: 'design',
          title: 'Create design',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/design.md'],
            prompt: { inline: 'Create the design artifact for issue #{{ issue.number }}: {{ issue.title }}.\n\nWrite the result to: {{ artifacts.openspecChange }}/design.md\n\nUse the existing change artifacts in {{ artifacts.openspecChange }} as context when relevant.' },
          },
        },
        {
          id: 'tasks',
          title: 'Generate tasks',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/tasks.json'],
            prompt: { inline: 'Create the tasks.json implementation plan artifact for issue #{{ issue.number }}: {{ issue.title }}.\n\nWrite the result to: {{ artifacts.openspecChange }}/tasks.json\n\nUse the existing change artifacts in {{ artifacts.openspecChange }} as context when relevant.' },
          },
        },
        {
          id: 'self-review',
          title: 'Self review',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/self-review.md'],
            prompt: { inline: 'Create the self-review artifact for issue #{{ issue.number }}: {{ issue.title }}.\n\nWrite the result to: {{ artifacts.openspecChange }}/self-review.md\n\nReview proposal.md, specs, design.md, and tasks.json.\nEnd the file with exactly one marker: <promise>PASS</promise> or <promise>FAIL</promise>.' },
          },
        },
      ],
      checks: [
        { name: 'proposal-complete', title: 'Proposal complete', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/proposal.md' } },
        { name: 'specs-complete', title: 'Specs complete', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/specs' } },
        { name: 'design-complete', title: 'Design complete', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/design.md' } },
        { name: 'tasks-valid', title: 'Tasks valid', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/tasks.json' } },
        {
          name: 'self-review-passed',
          title: 'Self review passed',
          uses: 'mohist/marker',
          with: {
            path: '{{ artifacts.openspecChange }}/self-review.md',
            expect: '<promise>PASS</promise>',
          },
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-plan-review',
                title: 'Fix plan review findings',
                uses: 'mohist/agent',
                onSuccess: { emit: ['plan.changed'] },
                with: {
                  prompt: {
                    inline: 'Fix the plan review findings in:\n\n{{ artifacts.openspecChange }}/self-review.md\n\nApply the minimal artifact changes required under:\n{{ artifacts.openspecChange }}\n\nDo not edit self-review.md.\nThe workflow will run self-review again after your artifact changes.',
                  },
                },
              },
            },
          },
        },
        {
          name: 'health:plan',
          title: 'Plan health gate',
          uses: 'mohist/health-gate',
          with: { command: DEFAULT_PLAN_HEALTH_COMMAND, timeout: DEFAULT_HEALTH_TIMEOUT_MS },
        },
      ],
      requiresApproval: true,
    },
    {
      stage: Stage.Build,
      tasks: [],
      tasksFrom: {
        uses: 'mohist/openspec-tasks',
        with: {
          path: '{{ artifacts.openspecChange }}/tasks.json',
          task: {
            uses: 'mohist/agent',
            with: {
              session: 'build',
              prompt: {
                inline: '<task>\n  <id>{{ task.id }}</id>\n  <title>{{ task.title }}</title>\n  <description>{{ task.description }}</description>\n  <acceptanceCriteria>\n{{ task.acceptanceCriteria }}\n  </acceptanceCriteria>\n  <changeDir>{{ artifacts.openspecChange }}</changeDir>\n</task>\n\nImplement this task in the current worktree.\nSatisfy every acceptance criterion.\nKeep the change scoped to this task.',
              },
            },
          },
        },
      },
      checks: [
        {
          name: 'health:build',
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
                with: {
                  prompt: {
                    inline: 'Fix the build health failure.\n\nRun or inspect the configured build command, apply the minimal code or artifact changes required, and avoid unrelated refactors.',
                  },
                },
              },
            },
          },
        },
      ],
    },
    {
      stage: Stage.Check,
      tasks: [
        {
          id: 'ai-review',
          title: 'AI review',
          uses: 'mohist/check/ai-review',
        },
      ],
      checks: [
        {
          name: 'health:check',
          title: 'Check health gate',
          uses: 'mohist/health-gate',
          with: {
            command: DEFAULT_CHECK_HEALTH_COMMAND,
            timeout: DEFAULT_HEALTH_TIMEOUT_MS,
          },
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-check-health',
                title: 'Fix check health',
                uses: 'mohist/agent',
                with: {
                  prompt: {
                    inline: 'Fix the check health failure.\n\nRun or inspect the configured check command, apply the minimal code changes required, and avoid unrelated refactors.',
                  },
                },
              },
            },
          },
        },
        {
          name: 'review-passed',
          title: 'Review passed',
          uses: 'mohist/marker',
          with: {
            path: '{{ artifacts.openspecChange }}/review.md',
            expect: '<promise>PASS</promise>',
          },
          onFailure: {
            retry: {
              limit: 2,
              task: {
                id: 'fix-review-findings',
                title: 'Fix review findings',
                uses: 'mohist/agent',
                with: {
                  prompt: {
                    inline: 'Fix the blocking findings in:\n\n{{ artifacts.openspecChange }}/review.md\n\nApply the minimal code changes required.\nDo not edit review.md.',
                  },
                },
              },
            },
          },
        },
        {
          name: 'merge-ready',
          title: 'Merge ready',
          uses: 'mohist/merge-ready',
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-merge-readiness',
                title: 'Fix merge readiness',
                uses: 'mohist/rebase',
              },
            },
          },
        },
      ],
      requiresApproval: true,
    },
    {
      stage: Stage.Integrate,
      tasks: [
        { id: 'integrate:spec-sync', title: 'Sync specs', uses: 'mohist/openspec-sync' },
        { id: 'integrate:archive-change', title: 'Archive change', uses: 'mohist/archive-change' },
        { id: 'integrate:merge', title: 'Merge branch', uses: 'mohist/merge' },
      ],
      checks: [
        {
          name: 'health:integrate',
          title: 'Post-delivery health check',
          uses: 'mohist/health-gate',
          with: { command: DEFAULT_CHECK_HEALTH_COMMAND, timeout: DEFAULT_HEALTH_TIMEOUT_MS },
          onFailure: {
            retry: {
              limit: 1,
              task: {
                id: 'fix-integrate-health',
                title: 'Fix integrate health',
                uses: 'mohist/agent',
                with: {
                  prompt: {
                    inline: 'Fix the post-delivery health failure.\n\nApply the minimal changes required after integration side effects. Preserve already completed delivery work unless correcting the health failure requires it.',
                  },
                },
              },
            },
          },
        },
      ],
    },
  ],
};

export function getDefaultStageDefinition(stage: string): StageDefinition | undefined {
  return MOHIST_DEFAULT_WORKFLOW_DEFINITION.stages.find(s => s.stage === stage);
}
