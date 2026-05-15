import { describe, expect, it } from 'vitest';
import { Stage } from '../src/types';
import { freezePointFromStageSnapshot } from '../src/workflow/domain/persistence';
import type { StageRunSnapshot } from '../src/workflow/domain';

describe('workflow run persistence helpers', () => {
  it('reconstructs Integrate freeze point from service-call wrapped merge output', () => {
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
      freezePoint: null,
    };

    expect(freezePointFromStageSnapshot(Stage.Integrate, snapshot)?.delivery).toEqual({
      targetBranch: 'main',
      baseSha: 'base',
      candidateHeadSha: 'candidate',
      landedSha: 'landed',
      rebased: true,
    });
  });
});
