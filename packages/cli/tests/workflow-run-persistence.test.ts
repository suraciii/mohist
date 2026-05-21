import { describe, expect, it } from 'vitest';
import { Stage } from '../src/types';
import { commitPointFromStageSnapshot, hydrateWorkflowRun } from '../src/workflow/projection/workflow-run-snapshot';
import type { StageDefinition, StageRunSnapshot, WorkflowRunSnapshot } from '../src/workflow/model';

describe('workflow run persistence helpers', () => {
  it('reconstructs Integrate commit point from service-call wrapped merge output', () => {
    const definition: StageDefinition = {
      stage: Stage.Integrate,
      tasks: [{ id: 'integrate:merge', title: 'Merge branch', uses: 'mohist/merge' }],
      checks: [],
    };
    const snapshot: StageRunSnapshot = {
      stage: Stage.Integrate,
      status: 'running',
      order: 3,
      tasks: [{
        id: 'integrate:merge',
        title: 'Merge branch',
        status: 'completed',
        order: 2,
        dependsOn: [],
        attempts: 1,
        duration: 10,
        artifacts: [],
        output: {
          kind: 'service-call-task',
          result: {
            targetBranch: 'main',
            baseSha: 'base',
            candidateHeadSha: 'candidate',
            landedSha: 'landed',
            rebased: true,
          },
        },
        reason: null,
        causedBy: null,
      }],
      checks: [],
      approval: null,
      failure: null,
      commitPoint: null,
    };

    expect(commitPointFromStageSnapshot(Stage.Integrate, snapshot, definition)).toMatchObject({
      taskId: 'integrate:merge',
      uses: 'mohist/merge',
      metadata: {
        targetBranch: 'main',
        baseSha: 'base',
        candidateHeadSha: 'candidate',
        landedSha: 'landed',
        rebased: true,
      },
    });
  });

  it('reconstructs custom commit point and post-commit failure after hydrate', () => {
    const definition: StageDefinition = {
      stage: Stage.Check,
      tasks: [],
      checks: [
        { name: 'pr-merged', title: 'PR merged', uses: 'mohist/pr-merged' },
        { name: 'delivery-health', title: 'Delivery health', uses: 'mohist/health-gate' },
      ],
      checkPolicies: [
        { checkName: 'pr-merged', phase: 'post-task' },
        { checkName: 'delivery-health', phase: 'post-task' },
      ],
      requiresApproval: false,
    };
    const stage: StageRunSnapshot = {
      stage: Stage.Check,
      status: 'failed',
      order: 0,
      tasks: [],
      checks: [
        {
          name: 'pr-merged',
          title: 'PR merged',
          status: 'passed',
          message: null,
          output: { mergedSha: 'remote-landed' },
          runCount: 1,
        },
        {
          name: 'delivery-health',
          title: 'Delivery health',
          status: 'failed',
          message: 'remote verification failed',
          output: null,
          runCount: 1,
        },
      ],
      approval: null,
      failure: null,
      commitPoint: null,
    };
    const snapshot: WorkflowRunSnapshot = {
      id: 'run-custom',
      issueId: 'issue-custom',
      issueNumber: 42,
      status: 'failed',
      currentStage: Stage.Check,
      stageOrder: [Stage.Check],
      workflowDefinitionSnapshot: {
        workflowId: 'custom/remote',
        source: { type: 'runtime', id: 'custom/remote' },
        resolvedDefinition: { id: 'custom/remote', stages: [definition] },
        compiledStageDefinitions: [definition],
        capturedAt: '2026-05-19T00:00:00.000Z',
      },
      stageRuns: [stage],
      failure: null,
    };

    expect(commitPointFromStageSnapshot(Stage.Check, stage, definition)).toMatchObject({
      checkName: 'pr-merged',
      uses: 'mohist/pr-merged',
      metadata: { mergedSha: 'remote-landed' },
    });

    const run = hydrateWorkflowRun(snapshot);
    expect(run.stageRun(Stage.Check).commitPoint).toMatchObject({
      checkName: 'pr-merged',
      uses: 'mohist/pr-merged',
      metadata: { mergedSha: 'remote-landed' },
    });
    expect(run.failure).toMatchObject({
      reason: 'post-commit-check-failed',
      checkName: 'delivery-health',
    });
  });
});
