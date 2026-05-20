import type { ResultContract } from '../../types/workflow-results';

export const REVIEW_RESULT_CONTRACT: ResultContract = {
  kind: 'promise-marker',
  required: true,
  outputSource: { type: 'artifact', path: 'review.md' },
  allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
};

export const SELF_REVIEW_RESULT_CONTRACT: ResultContract = {
  kind: 'promise-marker',
  required: true,
  outputSource: { type: 'artifact', path: 'self-review.md' },
  allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
};
