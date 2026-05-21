import { describe, it, expect } from 'vitest';
import { extractRepairResultFromArtifact } from '../../src/workflow/tasks/self-repair';
import type { ResultContract } from '../../src/types/workflow-results';

function makeContract(path = 'review.md'): ResultContract {
  return {
    kind: 'promise-marker',
    required: true,
    outputSource: { type: 'artifact', path },
    allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
  };
}

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

      expect(result.hadRepairs).toBe(false);
      expect(result.repairedItemIds).toEqual([]);
      expect(result.repairedItems).toEqual([]);
      expect(result.verification).toEqual([]);
    });

    it('returns empty result for null artifact', () => {
      const result = extractRepairResultFromArtifact(makeContract(), null);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

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

      const result = extractRepairResultFromArtifact(makeContract(), content);

      expect(result.repairedItemIds).toEqual(['fix-1']);
      expect(result.unresolvedItems).toHaveLength(0);
      expect(result.postRepairVerdict).toBe('PASS');
    });
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
