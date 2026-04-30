import { describe, it, expect } from 'vitest';
import { parseVerdict } from '../src/workflow';
import { buildAutoFixPrompt } from '../src/agents/artifact-prompt';
import type { Issue } from '../src/types';

const mockIssue: Issue = {
  id: 'issue-1',
  number: 42,
  title: 'Add auto-fix flow',
  body: 'Implement auto-fix after self-check FAIL.',
  stage: 'plan' as any,
  status: 'active' as any,
  projectId: 'proj-1',
  labels: [],
  createdAt: '2024-01-01T00:00:00Z',
  updatedAt: '2024-01-01T00:00:00Z',
};

describe('parseVerdict', () => {
  it('should return PASS for explicit PASS verdict', () => {
    const content = '# Review\n\n## Verdict: PASS\n\nAll checks passed.';
    expect(parseVerdict(content)).toBe('PASS');
  });

  it('should return FAIL for explicit FAIL verdict', () => {
    const content = '# Review\n\n## Verdict: FAIL\n\nMissing spec compliance.';
    expect(parseVerdict(content)).toBe('FAIL');
  });

  it('should return null when verdict line is missing', () => {
    const content = '# Review\n\nSome content without a verdict.';
    expect(parseVerdict(content)).toBeNull();
  });

  it('should return null for empty content', () => {
    expect(parseVerdict('')).toBeNull();
  });

  it('should handle extra whitespace after colon', () => {
    const content = '## Verdict:   PASS';
    expect(parseVerdict(content)).toBe('PASS');
  });

  it('should handle extra whitespace before verdict', () => {
    const content = '## Verdict: FAIL';
    expect(parseVerdict(content)).toBe('FAIL');
  });

  it('should match verdict mid-document', () => {
    const content = `# Self-Review

Some review content here.

## Verdict: PASS

## Summary
Everything looks good.`;
    expect(parseVerdict(content)).toBe('PASS');
  });

  it('should return null for partial match like FAILURE', () => {
    const content = '## Verdict: FAILURE';
    expect(parseVerdict(content)).toBeNull();
  });

  it('should be case-sensitive — lowercase pass does not match', () => {
    const content = '## Verdict: pass';
    expect(parseVerdict(content)).toBe('PASS');
  });
});

describe('buildAutoFixPrompt', () => {
  const changeDir = '/tmp/test-change';
  const reportContent = `# Review Self-Check

## Verdict: FAIL

## Issues
- Missing spec compliance check
- Formatting errors in output`;

  it('should include issue info', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'review.md');

    expect(result).toContain('Issue #42');
    expect(result).toContain('Add auto-fix flow');
  });

  it('should include change directory', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'review.md');

    expect(result).toContain(changeDir);
  });

  it('should include report content', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'review.md');

    expect(result).toContain('Missing spec compliance check');
    expect(result).toContain('Formatting errors in output');
  });

  it('should include report file name in task description', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'self-review.md');

    expect(result).toContain('self-review.md');
    expect(result).toContain('FAIL verdict');
  });

  it('should reference changeDir in fix instructions', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'review.md');

    expect(result).toContain(`Edit the relevant files in ${changeDir}`);
  });

  it('should work with empty report content', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, '', 'review.md');

    expect(result).toContain('FAIL verdict');
    expect(result).toContain(changeDir);
  });

  it('should handle different report file names', () => {
    const result = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'self-review.md');
    const result2 = buildAutoFixPrompt(mockIssue, changeDir, reportContent, 'review.md');

    expect(result).toContain('self-review.md');
    expect(result2).toContain('review.md');
    expect(result).toContain('Report (self-review.md)');
    expect(result2).toContain('Report (review.md)');
  });
});
