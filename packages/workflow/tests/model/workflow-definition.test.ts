import { describe, expect, it } from 'vitest';
import {
  WorkflowDomainError,
  cloneWorkflowDefinitionSnapshot,
  compileWorkflowDefinition,
  createWorkflowDefinitionSnapshot,
  type WorkflowDefinition,
} from '../../src';

describe('workflow definition model', () => {
  it('compiles check failure retry and event reset policies from semantic stage definitions', () => {
    const compiled = compileWorkflowDefinition({
      id: 'custom/review',
      stages: [
        {
          stage: 'review',
          tasks: [
            { id: 'review', title: 'Review', uses: 'custom/review' },
          ],
          checks: [
            {
              name: 'review-passed',
              title: 'Review passed',
              uses: 'custom/marker',
              onFailure: {
                retry: {
                  limit: 2,
                  task: {
                    id: 'fix-review',
                    title: 'Fix review findings',
                    uses: 'custom/agent',
                  },
                },
              },
            },
          ],
          on: {
            'code.changed': {
              reset: {
                checks: 'all',
                approval: true,
              },
            },
          },
          requiresApproval: true,
        },
      ],
    });

    expect(compiled[0].checkFailurePolicies).toEqual([
      {
        checkName: 'review-passed',
        retryTaskId: 'fix-review',
        retryTaskTitle: 'Fix review findings',
        maxAttempts: 2,
        inputFrom: undefined,
      },
    ]);
    expect(compiled[0].approvalPolicy).toEqual({ checkName: 'user-approval' });
    expect(compiled[0].checkPolicies).toEqual([
      { checkName: 'review-passed', phase: 'post-task' },
    ]);
    expect(compiled[0].invalidationPolicy).toEqual({
      entries: [
        {
          trigger: 'task-completion',
          eventName: 'code.changed',
          reason: 'code.changed reset',
          invalidates: {
            checks: ['review-passed'],
            approval: true,
          },
        },
      ],
    });
  });

  it('defensively clones workflow definition snapshots', () => {
    const source: WorkflowDefinition = {
      id: 'custom/snapshot',
      stages: [
        {
          stage: 'plan',
          tasks: [{ id: 'proposal', title: 'Proposal', with: { prompt: 'write proposal' } }],
          checks: [{ name: 'proposal-ok', title: 'Proposal OK' }],
        },
      ],
    };
    const snapshot = createWorkflowDefinitionSnapshot({
      definition: source,
      capturedAt: '2026-05-21T00:00:00.000Z',
    });

    source.stages[0].tasks[0].title = 'Mutated after capture';
    snapshot.resolvedDefinition.stages[0].tasks[0].title = 'Mutated snapshot';

    const cloned = cloneWorkflowDefinitionSnapshot(snapshot);
    cloned.compiledStageDefinitions[0].tasks[0].title = 'Mutated clone';

    const fresh = createWorkflowDefinitionSnapshot({
      definition: {
        id: 'custom/snapshot',
        stages: [
          {
            stage: 'plan',
            tasks: [{ id: 'proposal', title: 'Proposal', with: { prompt: 'write proposal' } }],
            checks: [{ name: 'proposal-ok', title: 'Proposal OK' }],
          },
        ],
      },
      capturedAt: '2026-05-21T00:00:00.000Z',
    });

    expect(fresh.resolvedDefinition.stages[0].tasks[0].title).toBe('Proposal');
    expect(fresh.compiledStageDefinitions[0].tasks[0].title).toBe('Proposal');
  });

  it('rejects invalid workflow definition shapes before runtime use', () => {
    expect(() => compileWorkflowDefinition({ id: '', stages: [] })).toThrow(WorkflowDomainError);
    expect(() => compileWorkflowDefinition({
      id: 'invalid/empty',
      stages: [],
    })).toThrow(/requires at least one stage/);
    expect(() => compileWorkflowDefinition({
      id: 'invalid/duplicate-stage',
      stages: [
        { stage: 'build', tasks: [], checks: [] },
        { stage: 'build', tasks: [], checks: [] },
      ],
    })).toThrow(/duplicate stage/);
    expect(() => compileWorkflowDefinition({
      id: 'invalid/approval-check',
      stages: [
        {
          stage: 'plan',
          tasks: [],
          checks: [],
          requiresApproval: true,
          approvalCheckName: 'missing-approval',
        },
      ],
    })).toThrow(/approval references unknown check/);
  });
});
