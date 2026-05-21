import { Stage } from '../../../../types';
import { REVIEW_RESULT_CONTRACT, SELF_REVIEW_RESULT_CONTRACT } from './review-contracts';
import {
  compileWorkflowDefinition,
  createWorkflowDefinitionSnapshot,
  type WorkflowDefinition,
} from '../../../model';
import {
  compileRuntimeStageDefinitions,
  compileRuntimeWorkflowDefinitionSnapshot,
  type RuntimeStageDefinition,
  type RuntimeWorkflowDefinitionSnapshot,
} from '../../../runner/workflow-runtime-definition';
import {
  parseWorkflowDefinitionSource,
  workflowDefinitionSourceToYaml,
  type WorkflowSourceDefinition,
} from '../../../definition/workflow-definition-source';

const DEFAULT_PLAN_HEALTH_COMMAND = 'npm ci && npm run typecheck';
const DEFAULT_BUILD_HEALTH_COMMAND = 'npm ci && npm run build';
const DEFAULT_CHECK_HEALTH_COMMAND = 'npm ci && npm run build && npm test';
const DEFAULT_HEALTH_TIMEOUT_MS = 5 * 60 * 1000;

function planArtifactPrompt(artifactName: string, outputPath: string, extraInstructions: string[] = []): string {
  return [
    `Create the ${artifactName} artifact for issue #{{ issue.number }}: {{ issue.title }}.`,
    '',
    `Write the result to: ${outputPath}`,
    '',
    'Use the existing change artifacts in {{ artifacts.openspecChange }} as context when relevant.',
    ...extraInstructions.length > 0 ? ['', ...extraInstructions] : [],
  ].join('\n');
}

export const MOHIST_DEFAULT_WORKFLOW_SOURCE: WorkflowSourceDefinition = {
  id: 'mohist/default',
  name: 'Mohist default issue delivery workflow',
  artifacts: {
    openspecChange: '{{ openspec.changeDir }}',
  },
  stages: [
    {
      id: Stage.Plan,
      on: {
        'plan.changed': {
          reset: {
            tasks: ['self-review'],
            checks: ['self-review-passed'],
            approval: true,
          },
        },
      },
      tasks: [
        {
          id: 'proposal',
          title: 'Generate proposal',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/proposal.md'],
            prompt: { inline: planArtifactPrompt('proposal', '{{ artifacts.openspecChange }}/proposal.md') },
          },
        },
        {
          id: 'specs',
          title: 'Write specs',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/specs'],
            prompt: { inline: planArtifactPrompt('specs', '{{ artifacts.openspecChange }}/specs') },
          },
        },
        {
          id: 'design',
          title: 'Create design',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/design.md'],
            prompt: { inline: planArtifactPrompt('design', '{{ artifacts.openspecChange }}/design.md') },
          },
        },
        {
          id: 'tasks',
          title: 'Generate tasks',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/tasks.json'],
            prompt: { inline: planArtifactPrompt('tasks.json implementation plan', '{{ artifacts.openspecChange }}/tasks.json') },
          },
        },
        {
          id: 'self-review',
          title: 'Self review',
          uses: 'mohist/agent',
          with: {
            session: 'plan-artifacts',
            outputs: ['{{ artifacts.openspecChange }}/self-review.md'],
            prompt: {
              inline: planArtifactPrompt('self-review', '{{ artifacts.openspecChange }}/self-review.md', [
                'Review proposal.md, specs, design.md, and tasks.json.',
                'End the file with exactly one marker: <promise>PASS</promise> or <promise>FAIL</promise>.',
              ]),
            },
            requiredMarkers: [
              {
                path: '{{ artifacts.openspecChange }}/self-review.md',
                markers: SELF_REVIEW_RESULT_CONTRACT.allowedMarkers,
                onMissing: { action: 'continue-session', maxAttempts: 1 },
              },
            ],
          },
        },
      ],
      checks: [
        { id: 'proposal-complete', title: 'Proposal complete', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/proposal.md' } },
        { id: 'specs-complete', title: 'Specs complete', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/specs' } },
        { id: 'design-complete', title: 'Design complete', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/design.md' } },
        { id: 'tasks-valid', title: 'Tasks valid', uses: 'mohist/artifact-exists', with: { path: '{{ artifacts.openspecChange }}/tasks.json' } },
        {
          id: 'self-review-passed',
          title: 'Self review passed',
          uses: 'mohist/marker',
          with: {
            path: '{{ artifacts.openspecChange }}/self-review.md',
            expect: '<promise>PASS</promise>',
            markers: SELF_REVIEW_RESULT_CONTRACT.allowedMarkers,
            verdicts: SELF_REVIEW_RESULT_CONTRACT.verdicts,
            format: 'mohist/self-review',
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
                    inline: [
                      'Fix the plan review findings in:',
                      '',
                      '{{ artifacts.openspecChange }}/self-review.md',
                      '',
                      'Apply the minimal artifact changes required under:',
                      '{{ artifacts.openspecChange }}',
                      '',
                      'Do not edit self-review.md.',
                      'The workflow will run self-review again after your artifact changes.',
                    ].join('\n'),
                  },
                },
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
      tasksFrom: {
        uses: 'mohist/openspec-tasks',
        with: {
          path: '{{ artifacts.openspecChange }}/tasks.json',
          task: {
            uses: 'mohist/agent',
            with: {
              session: 'build',
              prompt: {
                inline: [
                  '<task>',
                  '  <id>{{ task.id }}</id>',
                  '  <title>{{ task.title }}</title>',
                  '  <description>{{ task.description }}</description>',
                  '  <acceptanceCriteria>',
                  '{{ task.acceptanceCriteria }}',
                  '  </acceptanceCriteria>',
                  '  <changeDir>{{ artifacts.openspecChange }}</changeDir>',
                  '</task>',
                  '',
                  'Implement this task in the current worktree.',
                  'Satisfy every acceptance criterion.',
                  'Keep the change scoped to this task.',
                ].join('\n'),
              },
            },
          },
        },
      },
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
                with: {
                  prompt: {
                    inline: [
                      'Fix the build health failure.',
                      '',
                      'Run or inspect the configured build command, apply the minimal code or artifact changes required, and avoid unrelated refactors.',
                    ].join('\n'),
                  },
                },
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
          reset: {
            tasks: ['ai-review'],
            checks: 'all',
            approval: true,
          },
        },
      },
      tasks: [
        {
          id: 'ai-review',
          title: 'AI review',
          uses: 'mohist/check/ai-review',
          with: {
            requiredMarkers: [
              {
                path: '{{ artifacts.openspecChange }}/review.md',
                markers: REVIEW_RESULT_CONTRACT.allowedMarkers,
                onMissing: { action: 'continue-session', maxAttempts: 1 },
              },
            ],
          },
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
                    inline: [
                      'Fix the check health failure.',
                      '',
                      'Run or inspect the configured check command, apply the minimal code changes required, and avoid unrelated refactors.',
                    ].join('\n'),
                  },
                },
              },
            },
          },
        },
        {
          id: 'review-passed',
          title: 'Review passed',
          uses: 'mohist/marker',
          with: {
            path: '{{ artifacts.openspecChange }}/review.md',
            expect: '<promise>PASS</promise>',
            markers: REVIEW_RESULT_CONTRACT.allowedMarkers,
            verdicts: REVIEW_RESULT_CONTRACT.verdicts,
            format: 'mohist/review',
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
                    inline: [
                      'Fix the blocking findings in:',
                      '',
                      '{{ artifacts.openspecChange }}/review.md',
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
                    inline: [
                      'Fix the post-delivery health failure.',
                      '',
                      'Apply the minimal changes required after integration side effects. Preserve already completed delivery work unless correcting the health failure requires it.',
                    ].join('\n'),
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

export const MOHIST_DEFAULT_WORKFLOW_DEFINITION: WorkflowDefinition = parseWorkflowDefinitionSource(
  MOHIST_DEFAULT_WORKFLOW_SOURCE,
  { taskSource: 'builtin', checkSource: 'builtin' },
);

export const MOHIST_DEFAULT_WORKFLOW_YAML = workflowDefinitionSourceToYaml(MOHIST_DEFAULT_WORKFLOW_SOURCE);

export const DEFAULT_STAGE_DEFINITIONS: RuntimeStageDefinition[] = compileRuntimeStageDefinitions(
  compileWorkflowDefinition(MOHIST_DEFAULT_WORKFLOW_DEFINITION),
);

export function createDefaultWorkflowDefinitionSnapshot(capturedAt?: string): RuntimeWorkflowDefinitionSnapshot {
  return compileRuntimeWorkflowDefinitionSnapshot(createWorkflowDefinitionSnapshot({
    definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
    source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
    capturedAt,
  }));
}
