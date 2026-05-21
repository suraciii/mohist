import { describe, expect, it } from 'vitest';
import { buildFailedCheckContext } from '../../src';

describe('failed check reaction context', () => {
  it('extracts blocking items from structured check output', () => {
    const ctx = buildFailedCheckContext({
      name: 'review-passed',
      status: 'fail',
      output: {
        verdict: 'FAIL',
        structuredResult: {
          verdict: 'FAIL',
          items: [
            { id: 'item-1', severity: 'blocking', evidence: 'Missing error handling', suggestedAction: 'Add try-catch' },
            { id: 'item-2', severity: 'blocking', evidence: 'Unused import', suggestedAction: 'Remove import' },
            { id: 'item-3', severity: 'follow-up', evidence: 'Consider refactoring', status: 'open' },
          ],
        },
      },
    });

    expect(ctx.checkName).toBe('review-passed');
    expect(ctx.verdict).toBe('FAIL');
    expect(ctx.blockingItems.map(item => item.id)).toEqual(['item-1', 'item-2']);
    expect(ctx.nonBlockingItems.map(item => item.id)).toEqual(['item-3']);
  });

  it('separates pre-existing, out-of-scope, and resolved items from blocking work', () => {
    const ctx = buildFailedCheckContext({
      name: 'review-passed',
      status: 'fail',
      output: {
        verdict: 'FAIL',
        structuredResult: {
          verdict: 'FAIL',
          items: [
            { id: 'item-1', severity: 'blocking', evidence: 'Real issue' },
            { id: 'item-2', severity: 'blocking', evidence: 'Old issue', status: 'pre-existing' },
            { id: 'item-3', severity: 'blocking', evidence: 'Not in scope', status: 'out-of-scope' },
            { id: 'item-4', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
          ],
        },
      },
    });

    expect(ctx.blockingItems.map(item => item.id)).toEqual(['item-1']);
    expect(ctx.nonBlockingItems.map(item => item.id)).toEqual(['item-2', 'item-3']);
  });

  it('handles missing structured result without endpoint-specific assumptions', () => {
    const ctx = buildFailedCheckContext({
      name: 'custom-check',
      status: 'fail',
      output: { verdict: 'FAIL', report: 'Some text' },
    });

    expect(ctx.checkName).toBe('custom-check');
    expect(ctx.verdict).toBe('FAIL');
    expect(ctx.blockingItems).toEqual([]);
    expect(ctx.sourceArtifactRefs).toBeUndefined();
  });

  it('passes snapshot metadata and prior task outputs through', () => {
    const priorTaskOutputs = [{ taskId: 'review', status: 'completed', output: { summary: 'done' } }];
    const ctx = buildFailedCheckContext({
      name: 'review-passed',
      status: 'fail',
      output: {
        verdict: 'FAIL',
        structuredResult: {
          verdict: 'FAIL',
          snapshot: { sha: 'abc123', changedFiles: ['a.ts', 'b.ts'] },
          items: [],
        },
      },
    }, priorTaskOutputs);

    expect(ctx.snapshot?.sha).toBe('abc123');
    expect(ctx.priorTaskOutputs).toEqual(priorTaskOutputs);
  });

  it('passes multiple blocking items together', () => {
    const ctx = buildFailedCheckContext({
      name: 'review-passed',
      status: 'fail',
      output: {
        verdict: 'FAIL',
        structuredResult: {
          verdict: 'FAIL',
          items: [
            { id: 'block-1', severity: 'blocking', evidence: 'Issue A' },
            { id: 'block-2', severity: 'blocking', evidence: 'Issue B' },
            { id: 'block-3', severity: 'blocking', evidence: 'Issue C' },
            { id: 'warn-1', severity: 'warning', evidence: 'Minor issue' },
            { id: 'follow-1', severity: 'follow-up', evidence: 'Future work' },
          ],
        },
      },
    });

    expect(ctx.blockingItems.map(item => item.id)).toEqual(['block-1', 'block-2', 'block-3']);
  });
});
