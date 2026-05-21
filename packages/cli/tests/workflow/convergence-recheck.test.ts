import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  saveVerificationContext,
  loadVerificationContext,
  clearVerificationContext,
  extractReactionOutput,
  buildVerificationContextFromReaction,
  buildVerificationPromptSuffix,
  computeConvergenceState,
  type VerificationContext,
} from '../../src/workflow/reaction/convergence';
import type { ReactionTaskOutput, WorkflowItem, WorkflowConvergenceState } from '../../src/types/workflow-results';
import type { CheckResult, StageTaskResult } from '../../src/workflow/stage-context';

function makeFailedCheck(items: WorkflowItem[], verdict: 'PASS' | 'FAIL' = 'FAIL'): CheckResult {
  return {
    name: 'review-passed',
    status: 'fail',
    output: {
      verdict,
      structuredResult: {
        verdict,
        items,
      },
    },
  };
}

function makeReactionOutput(overrides: Partial<ReactionTaskOutput> = {}): ReactionTaskOutput {
  return {
    attemptedItemIds: [],
    resolvedItemIds: [],
    unresolvedItemIds: [],
    evidence: '',
    ...overrides,
  };
}

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

describe('Convergence recheck: T-007', () => {
  let changeDir: string;

  beforeEach(() => {
    changeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-convergence-'));
  });

  afterEach(() => {
    fs.rmSync(changeDir, { recursive: true, force: true });
  });

  describe('Verification context persistence', () => {
    it('saves and loads verification context', () => {
      const ctx: VerificationContext = {
        knownItemIds: ['item-1', 'item-2'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: ['item-2'],
        attemptedItemIds: ['item-1', 'item-2'],
        nonBlockingItemIds: ['item-3'],
        blockingItemIds: ['item-1', 'item-2'],
        failedCheckName: 'review-passed',
        reactionAttempt: 1,
      };

      saveVerificationContext(changeDir, ctx);
      const loaded = loadVerificationContext(changeDir);

      expect(loaded).toEqual(ctx);
    });

    it('returns null when no verification context exists', () => {
      expect(loadVerificationContext(changeDir)).toBeNull();
    });

    it('clears verification context', () => {
      const ctx: VerificationContext = {
        knownItemIds: ['item-1'],
        resolvedItemIds: [],
        unresolvedItemIds: ['item-1'],
        attemptedItemIds: ['item-1'],
        nonBlockingItemIds: [],
        blockingItemIds: ['item-1'],
        failedCheckName: 'review-passed',
        reactionAttempt: 1,
      };
      saveVerificationContext(changeDir, ctx);
      clearVerificationContext(changeDir);
      expect(loadVerificationContext(changeDir)).toBeNull();
    });
  });

  describe('extractReactionOutput', () => {
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

  describe('buildVerificationContextFromReaction', () => {
    it('builds context with blocking and non-blocking item IDs from failed check', () => {
      const failedCheck = makeFailedCheck([
        { id: 'item-1', severity: 'blocking', evidence: 'Bug 1' },
        { id: 'item-2', severity: 'blocking', evidence: 'Bug 2' },
        { id: 'item-3', severity: 'follow-up', evidence: 'Future work' },
      ]);

      const reaction = makeReactionOutput({
        attemptedItemIds: ['item-1', 'item-2'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: ['item-2'],
      });

      const ctx = buildVerificationContextFromReaction(failedCheck, reaction, 1);

      expect(ctx.knownItemIds).toContain('item-1');
      expect(ctx.knownItemIds).toContain('item-2');
      expect(ctx.blockingItemIds).toEqual(['item-1', 'item-2']);
      expect(ctx.nonBlockingItemIds).toEqual(['item-3']);
      expect(ctx.resolvedItemIds).toEqual(['item-1']);
      expect(ctx.unresolvedItemIds).toEqual(['item-2']);
      expect(ctx.failedCheckName).toBe('review-passed');
      expect(ctx.reactionAttempt).toBe(1);
    });

    it('excludes resolved and pre-existing items from blocking IDs', () => {
      const failedCheck = makeFailedCheck([
        { id: 'item-1', severity: 'blocking', evidence: 'Open bug' },
        { id: 'item-2', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
        { id: 'item-3', severity: 'blocking', evidence: 'Old issue', status: 'pre-existing' },
        { id: 'item-4', severity: 'blocking', evidence: 'Not in scope', status: 'out-of-scope' },
      ]);

      const reaction = makeReactionOutput({
        attemptedItemIds: ['item-1'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: [],
      });

      const ctx = buildVerificationContextFromReaction(failedCheck, reaction, 1);

      expect(ctx.blockingItemIds).toEqual(['item-1']);
      expect(ctx.nonBlockingItemIds).toContain('item-3');
      expect(ctx.nonBlockingItemIds).toContain('item-4');
    });
  });

  describe('buildVerificationPromptSuffix', () => {
    it('includes known items with resolved/unresolved status', () => {
      const ctx: VerificationContext = {
        knownItemIds: ['item-1', 'item-2'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: ['item-2'],
        attemptedItemIds: ['item-1', 'item-2'],
        nonBlockingItemIds: ['item-3'],
        blockingItemIds: ['item-1', 'item-2'],
        failedCheckName: 'review-passed',
        reactionAttempt: 1,
      };

      const suffix = buildVerificationPromptSuffix(ctx);

      expect(suffix).toContain('Verification Recheck');
      expect(suffix).toContain('[ID: item-1]');
      expect(suffix).toContain('RESOLVED');
      expect(suffix).toContain('[ID: item-2]');
      expect(suffix).toContain('UNRESOLVED');
      expect(suffix).toContain('[ID: item-3]');
      expect(suffix).toContain('Non-blocking');
    });

    it('includes verification rules', () => {
      const ctx: VerificationContext = {
        knownItemIds: ['item-1'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: [],
        attemptedItemIds: ['item-1'],
        nonBlockingItemIds: [],
        blockingItemIds: ['item-1'],
        failedCheckName: 'review-passed',
        reactionAttempt: 1,
      };

      const suffix = buildVerificationPromptSuffix(ctx);

      expect(suffix).toContain('Verify that each resolved item is actually fixed');
      expect(suffix).toContain('Only report NEW blockers');
      expect(suffix).toContain('PASS only if all previously blocking items are resolved');
    });
  });

  describe('computeConvergenceState', () => {
    it('computes state for fail -> reaction -> pass path', () => {
      const failedCheck = makeFailedCheck([
        { id: 'item-1', severity: 'blocking', evidence: 'Bug' },
        { id: 'item-2', severity: 'blocking', evidence: 'Another bug' },
      ]);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['item-1', 'item-2'],
        resolvedItemIds: ['item-1', 'item-2'],
        unresolvedItemIds: [],
      });

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'pass',
        output: {
          verdict: 'PASS',
          structuredResult: {
            verdict: 'PASS',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
              { id: 'item-2', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
            ],
          },
        },
      };

      const state = computeConvergenceState(failedCheck, [reaction], verificationResult);

      expect(state.failedCheck).toBe('review-passed');
      expect(state.blockingItemCount).toBe(2);
      expect(state.reactionAttempts).toBe(1);
      expect(state.resolvedItemIds).toEqual(['item-1', 'item-2']);
      expect(state.unresolvedItemIds).toEqual([]);
      expect(state.newBlockingItemIds).toEqual([]);
      expect(state.blockedReason).toBeUndefined();
    });

    it('computes state for fail -> reaction -> unresolved blocker path', () => {
      const failedCheck = makeFailedCheck([
        { id: 'item-1', severity: 'blocking', evidence: 'Hard bug' },
      ]);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['item-1'],
        resolvedItemIds: [],
        unresolvedItemIds: ['item-1'],
      });

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Still broken' },
            ],
          },
        },
      };

      const state = computeConvergenceState(failedCheck, [reaction], verificationResult);

      expect(state.failedCheck).toBe('review-passed');
      expect(state.unresolvedItemIds).toEqual(['item-1']);
      expect(state.resolvedItemIds).toEqual([]);
      expect(state.blockedReason).toContain('unresolved');
    });

    it('computes state with new blockers from verification', () => {
      const failedCheck = makeFailedCheck([
        { id: 'item-1', severity: 'blocking', evidence: 'Bug' },
      ]);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['item-1'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: [],
      });

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
              { id: 'regression-1', severity: 'blocking', evidence: 'New regression from fix' },
            ],
          },
        },
      };

      const state = computeConvergenceState(failedCheck, [reaction], verificationResult);

      expect(state.resolvedItemIds).toEqual(['item-1']);
      expect(state.newBlockingItemIds).toEqual(['regression-1']);
      expect(state.blockedReason).toContain('new blockers');
    });

    it('tracks non-blocking follow-up items', () => {
      const failedCheck = makeFailedCheck([
        { id: 'item-1', severity: 'blocking', evidence: 'Bug' },
        { id: 'follow-1', severity: 'follow-up', evidence: 'Consider refactoring' },
        { id: 'info-1', severity: 'info', evidence: 'Good pattern' },
      ]);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['item-1'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: [],
      });

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'pass',
        output: {
          verdict: 'PASS',
          structuredResult: {
            verdict: 'PASS',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
              { id: 'follow-1', severity: 'follow-up', evidence: 'Consider refactoring' },
            ],
          },
        },
      };

      const state = computeConvergenceState(failedCheck, [reaction], verificationResult);

      expect(state.nonBlockingItemIds).toContain('follow-1');
      expect(state.nonBlockingItemIds).toContain('info-1');
      expect(state.newBlockingItemIds).toEqual([]);
      expect(state.blockedReason).toBeUndefined();
    });

    it('tracks directly repaired count', () => {
      const failedCheck: CheckResult = {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Bug', status: 'resolved' },
            ],
            repairedItemIds: ['item-1'],
          },
        },
      };

      const state = computeConvergenceState(failedCheck, [], undefined);
      expect(state.directlyRepairedCount).toBe(1);
    });
  });

  describe('End-to-end convergence flow simulation', () => {
    it('fail -> reaction batch -> verification recheck -> pass', () => {
      const items: WorkflowItem[] = [
        { id: 'bug-1', severity: 'blocking', evidence: 'Missing null check' },
        { id: 'bug-2', severity: 'blocking', evidence: 'Unused variable' },
        { id: 'follow-1', severity: 'follow-up', evidence: 'Consider refactoring later' },
      ];

      const failedCheck = makeFailedCheck(items);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['bug-1', 'bug-2'],
        resolvedItemIds: ['bug-1', 'bug-2'],
        unresolvedItemIds: [],
        evidence: 'Added null check, removed unused variable',
      });

      const verCtx = buildVerificationContextFromReaction(failedCheck, reaction, 1);
      saveVerificationContext(changeDir, verCtx);

      const loaded = loadVerificationContext(changeDir);
      expect(loaded).not.toBeNull();
      expect(loaded!.knownItemIds).toContain('bug-1');
      expect(loaded!.knownItemIds).toContain('bug-2');
      expect(loaded!.resolvedItemIds).toEqual(['bug-1', 'bug-2']);
      expect(loaded!.nonBlockingItemIds).toEqual(['follow-1']);

      const promptSuffix = buildVerificationPromptSuffix(loaded!);
      expect(promptSuffix).toContain('bug-1');
      expect(promptSuffix).toContain('bug-2');
      expect(promptSuffix).toContain('RESOLVED');

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'pass',
        output: {
          verdict: 'PASS',
          structuredResult: {
            verdict: 'PASS',
            items: [
              { id: 'bug-1', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
              { id: 'bug-2', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
              { id: 'follow-1', severity: 'follow-up', evidence: 'Still valid follow-up' },
            ],
          },
        },
      };

      const convergenceState = computeConvergenceState(failedCheck, [reaction], verificationResult);
      expect(convergenceState.failedCheck).toBe('review-passed');
      expect(convergenceState.blockingItemCount).toBe(2);
      expect(convergenceState.reactionAttempts).toBe(1);
      expect(convergenceState.resolvedItemIds).toEqual(['bug-1', 'bug-2']);
      expect(convergenceState.unresolvedItemIds).toEqual([]);
      expect(convergenceState.newBlockingItemIds).toEqual([]);
      expect(convergenceState.nonBlockingItemIds).toContain('follow-1');
      expect(convergenceState.blockedReason).toBeUndefined();

      clearVerificationContext(changeDir);
      expect(loadVerificationContext(changeDir)).toBeNull();
    });

    it('fail -> reaction batch -> unresolved blocker keeps stage blocked', () => {
      const items: WorkflowItem[] = [
        { id: 'bug-1', severity: 'blocking', evidence: 'Deep architectural issue' },
        { id: 'bug-2', severity: 'blocking', evidence: 'Simple typo' },
      ];

      const failedCheck = makeFailedCheck(items);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['bug-1', 'bug-2'],
        resolvedItemIds: ['bug-2'],
        unresolvedItemIds: ['bug-1'],
        evidence: 'Fixed typo, architectural issue too complex',
      });

      const verCtx = buildVerificationContextFromReaction(failedCheck, reaction, 1);
      saveVerificationContext(changeDir, verCtx);

      const loaded = loadVerificationContext(changeDir);
      expect(loaded!.unresolvedItemIds).toEqual(['bug-1']);
      expect(loaded!.resolvedItemIds).toEqual(['bug-2']);

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'bug-1', severity: 'blocking', evidence: 'Still present' },
            ],
          },
        },
      };

      const convergenceState = computeConvergenceState(failedCheck, [reaction], verificationResult);
      expect(convergenceState.unresolvedItemIds).toEqual(['bug-1']);
      expect(convergenceState.resolvedItemIds).toEqual(['bug-2']);
      expect(convergenceState.blockedReason).toContain('unresolved');
      expect(convergenceState.newBlockingItemIds).toEqual([]);
    });

    it('fail -> reaction -> new blocker from regression keeps stage blocked', () => {
      const items: WorkflowItem[] = [
        { id: 'bug-1', severity: 'blocking', evidence: 'Original bug' },
      ];

      const failedCheck = makeFailedCheck(items);

      const reaction: ReactionTaskOutput = makeReactionOutput({
        attemptedItemIds: ['bug-1'],
        resolvedItemIds: ['bug-1'],
        unresolvedItemIds: [],
        newItemIds: ['regression-1'],
        evidence: 'Fixed bug-1 but introduced regression',
      });

      const verCtx = buildVerificationContextFromReaction(failedCheck, reaction, 1);
      expect(verCtx.knownItemIds).toContain('bug-1');

      const verificationResult: CheckResult = {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'bug-1', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
              { id: 'regression-1', severity: 'blocking', evidence: 'New regression from the fix' },
            ],
          },
        },
      };

      const convergenceState = computeConvergenceState(failedCheck, [reaction], verificationResult);
      expect(convergenceState.resolvedItemIds).toEqual(['bug-1']);
      expect(convergenceState.newBlockingItemIds).toEqual(['regression-1']);
      expect(convergenceState.blockedReason).toContain('new blockers');
    });
  });

  describe('Reaction output persistence on task result', () => {
    it('reaction task output has all required fields for persistence', () => {
      const reaction: ReactionTaskOutput = {
        attemptedItemIds: ['item-1', 'item-2'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: ['item-2'],
        newItemIds: ['item-new'],
        evidence: 'Partially resolved',
        summary: 'Fixed 1 of 2 items',
      };

      const taskResult: StageTaskResult = makeTaskResult(reaction);

      const extracted = extractReactionOutput(taskResult);
      expect(extracted).not.toBeNull();
      expect(extracted!.attemptedItemIds).toEqual(['item-1', 'item-2']);
      expect(extracted!.resolvedItemIds).toEqual(['item-1']);
      expect(extracted!.unresolvedItemIds).toEqual(['item-2']);
      expect(extracted!.newItemIds).toEqual(['item-new']);
      expect(extracted!.evidence).toBe('Partially resolved');
    });

    it('handles missing optional newItemIds', () => {
      const reaction: ReactionTaskOutput = {
        attemptedItemIds: ['item-1'],
        resolvedItemIds: ['item-1'],
        unresolvedItemIds: [],
        evidence: 'All resolved',
      };

      const taskResult = makeTaskResult(reaction);
      const extracted = extractReactionOutput(taskResult);
      expect(extracted).not.toBeNull();
      expect(extracted!.newItemIds).toEqual([]);
    });
  });
});
