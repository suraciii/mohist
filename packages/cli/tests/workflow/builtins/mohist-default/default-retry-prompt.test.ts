import { describe, expect, it } from 'vitest';
import { Stage } from '../../../../src/types';
import { DEFAULT_STAGE_DEFINITIONS } from '../../../../src/workflow/builtins/workflows/mohist-default';

describe('mohist default workflow retry prompt', () => {
  it('review-passed retry uses a plain review.md prompt instead of input selectors', () => {
    const checkStage = DEFAULT_STAGE_DEFINITIONS.find(stage => stage.stage === Stage.Check);
    expect(checkStage).toBeDefined();

    const reviewFailureTaskPolicy = checkStage?.checkFailurePolicies?.find(
      policy => policy.retryTaskId === 'fix-review-findings',
    );
    expect(reviewFailureTaskPolicy).toBeDefined();
    expect(reviewFailureTaskPolicy?.inputFrom).toBeUndefined();

    const reviewFailurePolicy = checkStage?.checkFailurePolicies?.find(
      policy => policy.checkName === 'review-passed',
    );
    expect(reviewFailurePolicy).toBeDefined();
    expect(reviewFailurePolicy?.inputFrom).toBeUndefined();

    const reviewCheck = checkStage?.checks.find(check => check.name === 'review-passed');
    expect(reviewCheck?.onFailure?.retry?.task.with).toMatchObject({
      prompt: {
        inline: expect.stringContaining('{{ artifacts.openspecChange }}/review.md'),
      },
    });
  });
});
