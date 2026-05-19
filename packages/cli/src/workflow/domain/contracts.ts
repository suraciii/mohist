import type { ResultContract, SelfRepairPolicy } from '../../types/workflow-results';

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

export const REVIEW_SELF_REPAIR_POLICY: SelfRepairPolicy = {
  enabled: true,
  allowedScopes: [
    'formatting',
    'typos',
    'missing-obvious-guards',
    'small-test-expectation-updates',
    'import-cleanup',
    'dead-code-removal',
  ],
  maxAttempts: 3,
  requiresVerification: true,
  disallowedReasons: [
    'product-behavior-change',
    'public-contract-modification',
    'data-safety-risk',
    'security-posture-change',
    'merge-strategy-change',
    'architectural-judgment-required',
    'cross-file-refactoring',
    'ambiguous-solution',
    'user-decision-required',
    'out-of-current-scope',
  ],
};
