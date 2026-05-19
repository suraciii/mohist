import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import type { CheckContext } from '../../src/workflow/stage-context';

function makeIssue() {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test issue',
    stage: 'check' as const,
    status: 'in_progress' as const,
    projectId: 'proj-1',
  };
}

function makeCheckContext(changeDir: string): CheckContext {
  return {
    issue: makeIssue() as any,
    changeDir,
    eventBus: { emit: vi.fn() } as any,
    projectId: 'proj-1',
    acpOptions: {},
  };
}

function writeArtifact(changeDir: string, filename: string, content: string) {
  fs.mkdirSync(changeDir, { recursive: true });
  fs.writeFileSync(path.join(changeDir, filename), content);
}

describe('judgment-checks: shared parser regression', () => {
  let changeDir: string;

  beforeEach(() => {
    changeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-judgment-check-'));
  });

  afterEach(() => {
    fs.rmSync(changeDir, { recursive: true, force: true });
  });

  describe('ReviewPassedCheck', () => {
    it('derives PASS from structured parser, not prose', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '## Findings\n\nNo findings.\n\n<promise>PASS</promise>\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('pass');
      expect(result.name).toBe('review-passed');
      expect((result.output as any).verdict).toBe('PASS');
      expect((result.output as any).structuredResult).toBeDefined();
      expect((result.output as any).structuredResult.verdict).toBe('PASS');
      expect((result.output as any).structuredResult.marker).toBe('<promise>PASS</promise>');
    });

    it('derives FAIL from structured parser', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '<promise>FAIL</promise>\n\n- [ID: bug-1]\n  Severity: blocking\n  Evidence: Missing error handling\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('fail');
      expect((result.output as any).verdict).toBe('FAIL');
      expect((result.output as any).structuredResult.items).toHaveLength(1);
      expect((result.output as any).structuredResult.items[0].id).toBe('bug-1');
    });

    it('returns error when marker is missing', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '## Review\n\nEverything looks good but no verdict marker.\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('No valid promise marker');
    });

    it('returns error when marker is malformed', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '<promise>PARTIAL</promise>\n\nSome text\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('Malformed');
    });

    it('returns error for duplicate markers', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '<promise>PASS</promise>\n\nThen later: <promise>FAIL</promise>\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('Multiple promise markers');
    });

    it('returns error when file is missing', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('review.md not found');
    });

    it('does not infer PASS from prose text', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '## Review\n\nAll checks passed. Everything looks great.\nVerdict: PASS\nNo issues found.\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('No valid promise marker');
    });

    it('malformed marker does not become implicit FAIL', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '<promise>MAYBE</promise>\n\nSome text\n');

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).not.toBe('fail');
      expect(result.status).toBe('error');
    });

    it('remains read-only and does not modify files', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', '<promise>PASS</promise>\n');
      const beforeMtime = fs.statSync(path.join(changeDir, 'review.md')).mtimeMs;

      await new Promise(r => setTimeout(r, 10));
      const check = new ReviewPassedCheck();
      await check.run(makeCheckContext(changeDir));

      const afterMtime = fs.statSync(path.join(changeDir, 'review.md')).mtimeMs;
      expect(afterMtime).toBe(beforeMtime);
    });
  });

  describe('SelfReviewPassedCheck', () => {
    it('derives PASS from structured parser', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '## Self Review\n\n<promise>PASS</promise>\n\n### Quality: PASS\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('pass');
      expect((result.output as any).verdict).toBe('PASS');
      expect((result.output as any).structuredResult).toBeDefined();
      expect((result.output as any).structuredResult.verdict).toBe('PASS');
    });

    it('derives FAIL from structured parser', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '<promise>FAIL</promise>\n\n- [ID: dim-1]\n  Severity: blocking\n  Evidence: Missing test coverage\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('fail');
      expect((result.output as any).verdict).toBe('FAIL');
      expect((result.output as any).structuredResult.items).toHaveLength(1);
    });

    it('returns error when marker is missing', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '## Self Review\n\nLooks good but no marker.\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('No valid promise marker');
    });

    it('returns error when marker is malformed', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '<promise>PARTIAL</promise>\n\nSome text\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('Malformed');
    });

    it('returns error for duplicate markers', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '<promise>PASS</promise>\n\nLater: <promise>FAIL</promise>\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('Multiple promise markers');
    });

    it('returns error when file is missing', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
      expect(result.message).toContain('not found or empty');
    });

    it('does not infer PASS from prose text', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '## Self Review\n\nEverything passed.\nVerdict: PASS\nGreat job!\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('error');
    });

    it('malformed marker does not become implicit FAIL', async () => {
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');
      writeArtifact(changeDir, 'self-review.md', '<promise>UNKNOWN</promise>\n\nText\n');

      const check = new SelfReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).not.toBe('fail');
      expect(result.status).toBe('error');
    });
  });

  describe('shared parser behavior across checks', () => {
    it('review and self-review both parse PASS identically', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');

      writeArtifact(changeDir, 'review.md', '<promise>PASS</promise>\n');
      writeArtifact(changeDir, 'self-review.md', '<promise>PASS</promise>\n');

      const reviewResult = await new ReviewPassedCheck().run(makeCheckContext(changeDir));
      const selfReviewResult = await new SelfReviewPassedCheck().run(makeCheckContext(changeDir));

      expect(reviewResult.status).toBe('pass');
      expect(selfReviewResult.status).toBe('pass');
      expect((reviewResult.output as any).structuredResult.verdict).toBe('PASS');
      expect((selfReviewResult.output as any).structuredResult.verdict).toBe('PASS');
      expect((reviewResult.output as any).structuredResult.marker).toBe((selfReviewResult.output as any).structuredResult.marker);
    });

    it('review and self-review both parse FAIL identically', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');

      const failContent = '<promise>FAIL</promise>\n\n- [ID: test-1]\n  Severity: blocking\n  Evidence: test evidence\n';
      writeArtifact(changeDir, 'review.md', failContent);
      writeArtifact(changeDir, 'self-review.md', failContent);

      const reviewResult = await new ReviewPassedCheck().run(makeCheckContext(changeDir));
      const selfReviewResult = await new SelfReviewPassedCheck().run(makeCheckContext(changeDir));

      expect(reviewResult.status).toBe('fail');
      expect(selfReviewResult.status).toBe('fail');
      expect((reviewResult.output as any).structuredResult.items).toHaveLength(1);
      expect((selfReviewResult.output as any).structuredResult.items).toHaveLength(1);
      expect((reviewResult.output as any).structuredResult.items[0].id).toBe('test-1');
      expect((selfReviewResult.output as any).structuredResult.items[0].id).toBe('test-1');
    });

    it('review and self-review both produce error for missing markers', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');

      writeArtifact(changeDir, 'review.md', 'No marker here');
      writeArtifact(changeDir, 'self-review.md', 'No marker here either');

      const reviewResult = await new ReviewPassedCheck().run(makeCheckContext(changeDir));
      const selfReviewResult = await new SelfReviewPassedCheck().run(makeCheckContext(changeDir));

      expect(reviewResult.status).toBe('error');
      expect(selfReviewResult.status).toBe('error');
      expect(reviewResult.message).toContain('No valid promise marker');
      expect(selfReviewResult.message).toContain('No valid promise marker');
    });

    it('review and self-review both produce error for duplicate markers', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');

      writeArtifact(changeDir, 'review.md', '<promise>PASS</promise> and <promise>FAIL</promise>');
      writeArtifact(changeDir, 'self-review.md', '<promise>PASS</promise> and <promise>FAIL</promise>');

      const reviewResult = await new ReviewPassedCheck().run(makeCheckContext(changeDir));
      const selfReviewResult = await new SelfReviewPassedCheck().run(makeCheckContext(changeDir));

      expect(reviewResult.status).toBe('error');
      expect(selfReviewResult.status).toBe('error');
    });

    it('review and self-review both produce error for malformed markers', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      const { SelfReviewPassedCheck } = await import('../../src/workflow/checks/self-review-passed-check');

      writeArtifact(changeDir, 'review.md', '<promise>MAYBE</promise>');
      writeArtifact(changeDir, 'self-review.md', '<promise>MAYBE</promise>');

      const reviewResult = await new ReviewPassedCheck().run(makeCheckContext(changeDir));
      const selfReviewResult = await new SelfReviewPassedCheck().run(makeCheckContext(changeDir));

      expect(reviewResult.status).toBe('error');
      expect(selfReviewResult.status).toBe('error');
    });
  });

  describe('structured item policy validation', () => {
    it('extracts blocking items from FAIL output', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');
      writeArtifact(changeDir, 'review.md', [
        '<promise>FAIL</promise>',
        '',
        '- [ID: block-1]',
        '  Severity: blocking',
        '  Evidence: Critical bug',
        '  Status: open',
        '',
        '- [ID: follow-1]',
        '  Severity: follow-up',
        '  Evidence: Minor improvement',
        '  Status: out-of-scope',
      ].join('\n'));

      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));

      expect(result.status).toBe('fail');
      const items = (result.output as any).structuredResult.items;
      expect(items).toHaveLength(2);
      expect(items[0].severity).toBe('blocking');
      expect(items[1].severity).toBe('follow-up');
    });
  });
});
