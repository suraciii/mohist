import { describe, it, expect } from 'vitest';
import { extractReactionOutput } from '../../src/workflow/reaction/convergence';
import type { StageTaskResult } from '../../src/workflow/stage-context';

function makeTaskResult(output: unknown): StageTaskResult {
  return {
    taskId: 'fix-review-findings',
    title: 'Fix review findings',
    status: 'completed',
    artifacts: [],
    attempts: 1,
    duration: 100,
    output,
  };
}

describe('reaction output extraction', () => {
  it('extracts reaction output from task result with structured data', () => {
    const taskResult = makeTaskResult({
      attemptedItemIds: ['item-1', 'item-2'],
      resolvedItemIds: ['item-1'],
      unresolvedItemIds: ['item-2'],
      evidence: 'Fixed item-1 by adding null check',
      summary: 'Resolved 1 of 2 items',
    });

    const output = extractReactionOutput(taskResult);
    expect(output).not.toBeNull();
    expect(output!.attemptedItemIds).toEqual(['item-1', 'item-2']);
    expect(output!.resolvedItemIds).toEqual(['item-1']);
    expect(output!.unresolvedItemIds).toEqual(['item-2']);
    expect(output!.evidence).toBe('Fixed item-1 by adding null check');
  });

  it('extracts reaction output from agent-session-task wrapper', () => {
    const taskResult = makeTaskResult({
      kind: 'agent-session-task',
      result: {
        attemptedItemIds: ['bug-1', 'bug-2', 'bug-3'],
        resolvedItemIds: ['bug-1', 'bug-2'],
        unresolvedItemIds: ['bug-3'],
        evidence: 'Fixed two of three',
      },
    });

    const output = extractReactionOutput(taskResult);
    expect(output).not.toBeNull();
    expect(output!.attemptedItemIds).toEqual(['bug-1', 'bug-2', 'bug-3']);
    expect(output!.resolvedItemIds).toEqual(['bug-1', 'bug-2']);
    expect(output!.unresolvedItemIds).toEqual(['bug-3']);
  });

  it('extracts reaction output from agent-session structured text', () => {
    const taskResult = makeTaskResult({
      kind: 'agent-session-task',
      result: {
        structuredOutput: [
          'Summary: Fixed the safe items',
          'Attempted Item IDs: bug-1, bug-2, bug-3',
          'Resolved Item IDs: bug-1, bug-2',
          'Unresolved Item IDs: bug-3',
          'Evidence: Added guards and tests',
        ].join('\n'),
      },
    });

    const output = extractReactionOutput(taskResult);
    expect(output).not.toBeNull();
    expect(output!.attemptedItemIds).toEqual(['bug-1', 'bug-2', 'bug-3']);
    expect(output!.resolvedItemIds).toEqual(['bug-1', 'bug-2']);
    expect(output!.unresolvedItemIds).toEqual(['bug-3']);
    expect(output!.summary).toBe('Fixed the safe items');
    expect(output!.evidence).toBe('Added guards and tests');
  });

  it('returns null when no reaction fields present', () => {
    const taskResult = makeTaskResult({ summary: 'Did some work' });
    expect(extractReactionOutput(taskResult)).toBeNull();
  });

  it('returns null when task output is null', () => {
    const taskResult: StageTaskResult = {
      taskId: 'fix-review-findings',
      title: 'Fix review findings',
      status: 'completed',
      artifacts: [],
      attempts: 1,
      duration: 100,
    };
    expect(extractReactionOutput(taskResult)).toBeNull();
  });

  it('extracts newItemIds when present', () => {
    const taskResult = makeTaskResult({
      attemptedItemIds: ['item-1'],
      resolvedItemIds: ['item-1'],
      unresolvedItemIds: [],
      newItemIds: ['new-1'],
      evidence: 'Found a new issue',
    });

    const output = extractReactionOutput(taskResult);
    expect(output).not.toBeNull();
    expect(output!.newItemIds).toEqual(['new-1']);
  });
});
