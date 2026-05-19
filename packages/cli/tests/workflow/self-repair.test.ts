import { describe, it, expect } from 'vitest';
import {
  extractRepairResultFromArtifact,
  isRepairAllowed,
} from '../../src/workflow/task-runtime/self-repair';
import type { SelfRepairPolicy, WorkflowItem } from '../../src/types/workflow-results';
import type { ResultContract } from '../../src/types/workflow-results';
import { REVIEW_SELF_REPAIR_POLICY } from '../../src/workflow/domain';

function makeContract(path = 'review.md'): ResultContract {
  return {
    kind: 'promise-marker',
    required: true,
    outputSource: { type: 'artifact', path },
    allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
  };
}

const testPolicy: SelfRepairPolicy = {
  enabled: true,
  allowedScopes: ['formatting', 'typos', 'missing-obvious-guards'],
  maxAttempts: 3,
  requiresVerification: true,
  disallowedReasons: [
    'product-behavior-change',
    'architectural-judgment-required',
    'data-safety-risk',
  ],
};

describe('self-repair: extractRepairResultFromArtifact', () => {
  describe('safe repair recording', () => {
    it('extracts repaired items with resolved status and verification', () => {
      const content = [
        '<promise>PASS</promise>',
        '',
        '- [ID: fix-1]',
        '  Severity: info',
        '  Scope: formatting',
        '  Evidence: Fixed missing semicolon on line 42',
        '  Verification: npm run build',
        '  Status: resolved',
        '',
        '- [ID: fix-2]',
        '  Severity: info',
        '  Scope: typos',
        '  Evidence: Fixed typo in error message',
        '  Verification: npm test',
        '  Status: resolved',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.hadRepairs).toBe(true);
      expect(result.repairedItemIds).toEqual(['fix-1', 'fix-2']);
      expect(result.repairedItems).toHaveLength(2);
      expect(result.repairedItems[0].id).toBe('fix-1');
      expect(result.repairedItems[0].status).toBe('resolved');
      expect(result.repairedItems[0].verification).toBe('npm run build');
      expect(result.repairedItems[1].id).toBe('fix-2');
      expect(result.verification).toHaveLength(2);
      expect(result.verification[0].checkName).toBe('repair:fix-1');
      expect(result.verification[0].status).toBe('pass');
      expect(result.verification[1].checkName).toBe('repair:fix-2');
      expect(result.postRepairVerdict).toBe('PASS');
    });

    it('records single repaired item correctly', () => {
      const content = [
        '<promise>FAIL</promise>',
        '',
        '- [ID: safe-fix]',
        '  Severity: info',
        '  Scope: missing-obvious-guards',
        '  Evidence: Added null check for user input',
        '  Verification: npm test -- --grep "input validation"',
        '  Status: resolved',
        '',
        '- [ID: unsafe-fix]',
        '  Severity: blocking',
        '  Evidence: Security vulnerability in auth flow [disallowed:data-safety-risk]',
        '  Status: unresolved',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.repairedItemIds).toEqual(['safe-fix']);
      expect(result.unresolvedItems).toHaveLength(1);
      expect(result.unresolvedItems[0].id).toBe('unsafe-fix');
      expect(result.postRepairVerdict).toBe('FAIL');
    });

    it('handles artifact with no repairs', () => {
      const content = [
        '<promise>PASS</promise>',
        '',
        'All checks passed. No repairs needed.',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.hadRepairs).toBe(false);
      expect(result.repairedItemIds).toEqual([]);
      expect(result.repairedItems).toEqual([]);
      expect(result.verification).toEqual([]);
    });

    it('returns empty result for null artifact', () => {
      const result = extractRepairResultFromArtifact(makeContract(), null, testPolicy);

      expect(result.hadRepairs).toBe(false);
      expect(result.repairedItemIds).toEqual([]);
      expect(result.postRepairVerdict).toBeNull();
    });
  });

  describe('unsafe item reporting', () => {
    it('classifies unresolved blocking items correctly', () => {
      const content = [
        '<promise>FAIL</promise>',
        '',
        '- [ID: arch-1]',
        '  Severity: blocking',
        '  Evidence: Requires architectural redesign [disallowed:architectural-judgment-required]',
        '  SuggestedAction: Refactor module boundaries',
        '  Status: unresolved',
        '',
        '- [ID: prod-1]',
        '  Severity: blocking',
        '  Evidence: Changes public API contract [disallowed:product-behavior-change]',
        '  SuggestedAction: Add compatibility layer',
        '  Status: open',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.hadRepairs).toBe(false);
      expect(result.unresolvedItems).toHaveLength(2);
      expect(result.unresolvedItems[0].id).toBe('arch-1');
      expect(result.unresolvedItems[1].id).toBe('prod-1');
      expect(result.postRepairVerdict).toBe('FAIL');
    });

    it('separates follow-up items from blocking items', () => {
      const content = [
        '<promise>PASS</promise>',
        '',
        '- [ID: fix-1]',
        '  Severity: info',
        '  Scope: formatting',
        '  Evidence: Fixed indentation',
        '  Verification: npm run build',
        '  Status: resolved',
        '',
        '- [ID: follow-1]',
        '  Severity: follow-up',
        '  Evidence: Consider extracting utility function',
        '  Status: follow-up',
        '',
        '- [ID: preexist-1]',
        '  Severity: info',
        '  Evidence: Pre-existing issue in unrelated module',
        '  Status: pre-existing',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.repairedItemIds).toEqual(['fix-1']);
      expect(result.unresolvedItems).toHaveLength(0);
      expect(result.allItems).toHaveLength(3);
    });

    it('cannot produce PASS with unverified repairs', () => {
      const content = [
        '<promise>FAIL</promise>',
        '',
        '- [ID: unverified-1]',
        '  Severity: blocking',
        '  Scope: formatting',
        '  Evidence: Fixed something but did not verify',
        '  Status: resolved',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.repairedItemIds).toEqual([]);
      expect(result.hadRepairs).toBe(false);
    });
  });

  describe('mixed repair and unresolved items', () => {
    it('correctly reports both repaired and unresolved items', () => {
      const content = [
        '<promise>FAIL</promise>',
        '',
        '- [ID: fmt-1]',
        '  Severity: info',
        '  Scope: formatting',
        '  Evidence: Fixed trailing whitespace',
        '  Verification: npm run lint',
        '  Status: resolved',
        '',
        '- [ID: sec-1]',
        '  Severity: blocking',
        '  Evidence: SQL injection vulnerability [disallowed:data-safety-risk]',
        '  SuggestedAction: Use parameterized queries',
        '  Verification: Run security audit',
        '  Status: unresolved',
        '',
        '- [ID: arch-2]',
        '  Severity: blocking',
        '  Evidence: Module coupling too high [disallowed:architectural-judgment-required]',
        '  SuggestedAction: Introduce interface abstraction',
        '  Status: open',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.repairedItemIds).toEqual(['fmt-1']);
      expect(result.unresolvedItems).toHaveLength(2);
      expect(result.postRepairVerdict).toBe('FAIL');
      expect(result.verification).toHaveLength(1);
      expect(result.verification[0].checkName).toBe('repair:fmt-1');
    });

    it('post-repair PASS is valid when all blocking items are resolved', () => {
      const content = [
        '<promise>PASS</promise>',
        '',
        '- [ID: fix-1]',
        '  Severity: info',
        '  Scope: typos',
        '  Evidence: Fixed typo in README',
        '  Verification: npm run build',
        '  Status: resolved',
        '',
        '- [ID: follow-1]',
        '  Severity: follow-up',
        '  Evidence: Consider adding more examples',
        '  Status: follow-up',
      ].join('\n');

      const result = extractRepairResultFromArtifact(makeContract(), content, testPolicy);

      expect(result.repairedItemIds).toEqual(['fix-1']);
      expect(result.unresolvedItems).toHaveLength(0);
      expect(result.postRepairVerdict).toBe('PASS');
    });
  });
});

describe('self-repair: isRepairAllowed', () => {
  it('allows items with allowed scopes', () => {
    const item: WorkflowItem = {
      id: 'item-1',
      severity: 'info',
      scope: 'formatting',
      evidence: 'Trailing whitespace',
    };

    const result = isRepairAllowed(testPolicy, item);
    expect(result.allowed).toBe(true);
  });

  it('allows items with no scope when allowed scopes exist', () => {
    const item: WorkflowItem = {
      id: 'item-1',
      severity: 'info',
      evidence: 'Something simple',
    };

    const result = isRepairAllowed(testPolicy, item);
    expect(result.allowed).toBe(true);
  });

  it('rejects items with disallowed reason in evidence', () => {
    const item: WorkflowItem = {
      id: 'item-1',
      severity: 'blocking',
      evidence: 'Changes product behavior [disallowed:product-behavior-change]',
    };

    const result = isRepairAllowed(testPolicy, item);
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('product-behavior-change');
  });

  it('rejects items with disallowed reason in suggestedAction', () => {
    const item: WorkflowItem = {
      id: 'item-1',
      severity: 'blocking',
      evidence: 'Module needs redesign',
      suggestedAction: 'Refactor module [disallowed:architectural-judgment-required]',
    };

    const result = isRepairAllowed(testPolicy, item);
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('architectural-judgment-required');
  });

  it('rejects when policy is disabled', () => {
    const disabledPolicy: SelfRepairPolicy = {
      enabled: false,
      allowedScopes: [],
      requiresVerification: true,
      disallowedReasons: [],
    };

    const item: WorkflowItem = {
      id: 'item-1',
      severity: 'info',
      evidence: 'Simple fix',
    };

    const result = isRepairAllowed(disabledPolicy, item);
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('disabled');
  });

  it('rejects items with scope not in allowed scopes', () => {
    const item: WorkflowItem = {
      id: 'item-1',
      severity: 'blocking',
      scope: 'cross-file-refactoring',
      evidence: 'Needs changes across 5 files',
    };

    const result = isRepairAllowed(testPolicy, item);
    expect(result.allowed).toBe(false);
    expect(result.reason).toContain('not in allowed scopes');
  });
});

describe('self-repair: REVIEW_SELF_REPAIR_POLICY in domain', () => {
  it('has conservative allowed scopes', () => {
    expect(REVIEW_SELF_REPAIR_POLICY.enabled).toBe(true);
    expect(REVIEW_SELF_REPAIR_POLICY.allowedScopes).toContain('formatting');
    expect(REVIEW_SELF_REPAIR_POLICY.allowedScopes).toContain('typos');
    expect(REVIEW_SELF_REPAIR_POLICY.allowedScopes).toContain('missing-obvious-guards');
    expect(REVIEW_SELF_REPAIR_POLICY.allowedScopes).not.toContain('product-behavior-change');
  });

  it('has comprehensive disallowed reasons', () => {
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('product-behavior-change');
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('data-safety-risk');
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('security-posture-change');
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('architectural-judgment-required');
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('ambiguous-solution');
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('user-decision-required');
    expect(REVIEW_SELF_REPAIR_POLICY.disallowedReasons).toContain('out-of-current-scope');
  });

  it('requires verification', () => {
    expect(REVIEW_SELF_REPAIR_POLICY.requiresVerification).toBe(true);
  });
});

describe('self-repair: review-passed-check with repair metadata', () => {
  it('includes repairedItemIds when review has in-session repairs', async () => {
    const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
    const changeDir = `/tmp/mohist-test-repair-${Date.now()}`;
    const fs = await import('fs');
    const path = await import('path');

    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'review.md'), [
      '<promise>PASS</promise>',
      '',
      '- [ID: fmt-1]',
      '  Severity: info',
      '  Scope: formatting',
      '  Evidence: Fixed trailing whitespace on line 10',
      '  Verification: npm run lint',
      '  Status: resolved',
    ].join('\n'));

    const check = new ReviewPassedCheck();
    const ctx = {
      issue: { id: '1', number: 1, title: 'Test', projectId: 'p1' },
      changeDir,
      eventBus: { emit: () => {} },
      projectId: 'p1',
      acpOptions: {},
    };

    const result = await check.run(ctx as any);

    expect(result.status).toBe('pass');
    const structured = (result.output as any).structuredResult;
    expect(structured.repairedItemIds).toEqual(['fmt-1']);
    expect(structured.verification).toHaveLength(1);
    expect(structured.verification[0].checkName).toBe('repair:fmt-1');

    fs.rmSync(changeDir, { recursive: true, force: true });
  });

  it('includes unresolved items when review reports unsafe items', async () => {
    const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
    const changeDir = `/tmp/mohist-test-repair-unsafe-${Date.now()}`;
    const fs = await import('fs');
    const path = await import('path');

    fs.mkdirSync(changeDir, { recursive: true });
    fs.writeFileSync(path.join(changeDir, 'review.md'), [
      '<promise>FAIL</promise>',
      '',
      '- [ID: arch-1]',
      '  Severity: blocking',
      '  Evidence: Requires module redesign [disallowed:architectural-judgment-required]',
      '  SuggestedAction: Refactor module boundaries',
      '  Status: unresolved',
      '',
      '- [ID: sec-1]',
      '  Severity: blocking',
      '  Evidence: SQL injection risk [disallowed:data-safety-risk]',
      '  SuggestedAction: Use parameterized queries',
      '  Status: open',
    ].join('\n'));

    const check = new ReviewPassedCheck();
    const ctx = {
      issue: { id: '1', number: 1, title: 'Test', projectId: 'p1' },
      changeDir,
      eventBus: { emit: () => {} },
      projectId: 'p1',
      acpOptions: {},
    };

    const result = await check.run(ctx as any);

    expect(result.status).toBe('fail');
    const structured = (result.output as any).structuredResult;
    expect(structured.items).toHaveLength(2);
    expect(structured.items[0].id).toBe('arch-1');
    expect(structured.items[1].id).toBe('sec-1');

    fs.rmSync(changeDir, { recursive: true, force: true });
  });
});
