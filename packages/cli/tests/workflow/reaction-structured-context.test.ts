import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

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

function makeCheckContext(changeDir: string) {
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

describe('Reaction structured context: T-006', () => {
  let changeDir: string;

  beforeEach(() => {
    changeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-reaction-ctx-'));
  });

  afterEach(() => {
    fs.rmSync(changeDir, { recursive: true, force: true });
  });

  describe('buildFailedCheckContext', () => {
    it('extracts blocking items from structured check output', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
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
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.checkName).toBe('review-passed');
      expect(ctx.verdict).toBe('FAIL');
      expect(ctx.blockingItems).toHaveLength(2);
      expect(ctx.blockingItems[0].id).toBe('item-1');
      expect(ctx.blockingItems[1].id).toBe('item-2');
      expect(ctx.nonBlockingItems).toHaveLength(1);
      expect(ctx.nonBlockingItems[0].id).toBe('item-3');
    });

    it('separates pre-existing and out-of-scope items as non-blocking', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
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
            ],
          },
        },
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.blockingItems).toHaveLength(1);
      expect(ctx.blockingItems[0].id).toBe('item-1');
      expect(ctx.nonBlockingItems).toHaveLength(2);
    });

    it('excludes resolved items from blocking', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Still broken' },
              { id: 'item-2', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
            ],
          },
        },
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.blockingItems).toHaveLength(1);
      expect(ctx.blockingItems[0].id).toBe('item-1');
    });

    it('handles missing structured result gracefully', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
        name: 'review-passed',
        status: 'fail',
        output: { verdict: 'FAIL', reviewReport: 'Some text' },
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.checkName).toBe('review-passed');
      expect(ctx.verdict).toBe('FAIL');
      expect(ctx.blockingItems).toHaveLength(0);
      expect(ctx.sourceArtifactRefs).toEqual(['review.md']);
    });

    it('passes snapshot metadata', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
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
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.snapshot).toBeDefined();
      expect(ctx.snapshot?.sha).toBe('abc123');
    });

    it('passes prior task outputs when provided', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const priorTaskOutputs = [{ taskId: 'ai-review', status: 'completed', output: { summary: 'done' } }];
      const failedCheck = {
        name: 'review-passed',
        status: 'fail',
        output: { verdict: 'FAIL' },
      };

      const ctx = buildFailedCheckContext(failedCheck, priorTaskOutputs);
      expect(ctx.priorTaskOutputs).toEqual(priorTaskOutputs);
    });
  });

  describe('repair-fix-adapter: structured prompt', () => {
    it('builds structured prompt with multiple blocking items', async () => {
      writeArtifact(changeDir, 'review.md', '<promise>FAIL</promise>');

      const { default: adapter } = await import('../../src/workflow/task-runtime/repair-fix-adapter');
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const capturedPrompts: string[] = [];
      const mockHandler = async (input: any) => {
        capturedPrompts.push(input.prompt);
        return {
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        };
      };

      const { createRepairFixAdapter } = await import('../../src/workflow/task-runtime/repair-fix-adapter');
      const sut = createRepairFixAdapter({ agentSessionHandler: mockHandler as any });

      const failedCheck = {
        name: 'review-passed',
        status: 'fail' as const,
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'bug-1', severity: 'blocking', evidence: 'Missing error handling', suggestedAction: 'Add try-catch' },
              { id: 'bug-2', severity: 'blocking', evidence: 'Unused variable', suggestedAction: 'Remove variable' },
              { id: 'follow-1', severity: 'follow-up', evidence: 'Consider refactoring' },
            ],
          },
        },
      };

      const mockCtx = {
        issue: makeIssue(),
        artifactManager: {
          getChangeDir: () => changeDir,
          exists: () => true,
        },
        eventBus: { emit: vi.fn() },
        acpOptions: {},
      } as any;

      const failedCheckContext = buildFailedCheckContext(failedCheck);
      await sut.dispatch('fix-review-findings', mockCtx, {
        worktreePath: changeDir,
        failedCheck,
        attempt: 1,
        failedCheckContext,
      });

      expect(capturedPrompts).toHaveLength(1);
      const prompt = capturedPrompts[0];
      expect(prompt).toContain('bug-1');
      expect(prompt).toContain('bug-2');
      expect(prompt).toContain('Missing error handling');
      expect(prompt).toContain('Unused variable');
      expect(prompt).toContain('Blocking Items (2)');
      expect(prompt).toContain('Non-blocking / Follow-up');
      expect(prompt).toContain('follow-1');
    });

    it('falls back to review report when no structured context available', async () => {
      const capturedPrompts: string[] = [];
      const mockHandler = async (input: any) => {
        capturedPrompts.push(input.prompt);
        return {
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        };
      };

      const { createRepairFixAdapter } = await import('../../src/workflow/task-runtime/repair-fix-adapter');
      const sut = createRepairFixAdapter({ agentSessionHandler: mockHandler as any });

      const failedCheck = {
        name: 'review-passed',
        status: 'fail' as const,
        output: {
          verdict: 'FAIL',
          reviewReport: '## Review Report\n\nSomething failed.',
          fixSuggestions: 'Fix the thing.',
        },
      };

      const mockCtx = {
        issue: makeIssue(),
        artifactManager: {
          getChangeDir: () => changeDir,
          exists: () => true,
        },
        eventBus: { emit: vi.fn() },
        acpOptions: {},
      } as any;

      await sut.dispatch('fix-review-findings', mockCtx, {
        worktreePath: changeDir,
        failedCheck,
        attempt: 1,
      });

      expect(capturedPrompts).toHaveLength(1);
      const prompt = capturedPrompts[0];
      expect(prompt).toContain('Review Report:');
      expect(prompt).toContain('Fix Suggestions:');
    });

    it('includes selected prior task outputs in repair prompt context', async () => {
      const capturedPrompts: string[] = [];
      const mockHandler = async (input: any) => {
        capturedPrompts.push(input.prompt);
        return {
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        };
      };

      const { createRepairFixAdapter } = await import('../../src/workflow/task-runtime/repair-fix-adapter');
      const sut = createRepairFixAdapter({ agentSessionHandler: mockHandler as any });

      const failedCheck = {
        name: 'review-passed',
        status: 'fail' as const,
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'bug-1', severity: 'blocking', evidence: 'Missing guard' },
            ],
          },
        },
      };

      const mockCtx = {
        issue: makeIssue(),
        artifactManager: {
          getChangeDir: () => changeDir,
          exists: () => true,
        },
        eventBus: { emit: vi.fn() },
        acpOptions: {},
      } as any;

      await sut.dispatch('fix-review-findings', mockCtx, {
        worktreePath: changeDir,
        failedCheck,
        attempt: 1,
        failedCheckContext: {
          checkName: 'review-passed',
          verdict: 'FAIL',
          blockingItems: [{ id: 'bug-1', severity: 'blocking', evidence: 'Missing guard' }],
          nonBlockingItems: [],
          priorTaskOutputs: [
            { source: 'failed-check-output', output: { verdict: 'FAIL' } },
            { source: 'snapshot', snapshot: { sha: 'abc123' } },
          ],
        },
      });

      expect(capturedPrompts).toHaveLength(1);
      expect(capturedPrompts[0]).toContain('Selected Prior Outputs:');
      expect(capturedPrompts[0]).toContain('failed-check-output');
      expect(capturedPrompts[0]).toContain('abc123');
    });
  });

  describe('review-fix-task: structured prompt', () => {
    it('builds structured prompt with blocking items from FailedCheckContext', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
        name: 'review-passed',
        status: 'fail' as const,
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Null pointer risk', suggestedAction: 'Add null check' },
              { id: 'item-2', severity: 'blocking', evidence: 'Missing test', suggestedAction: 'Add unit test' },
              { id: 'item-3', severity: 'blocking', evidence: 'Hardcoded value', suggestedAction: 'Use config' },
            ],
          },
        },
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.blockingItems).toHaveLength(3);
      expect(ctx.blockingItems.map(i => i.id)).toEqual(['item-1', 'item-2', 'item-3']);
    });
  });

  describe('Default Check retry prompt shape', () => {
    it('review-passed retry uses a plain review.md prompt instead of input selectors', async () => {
      const { DEFAULT_STAGE_DEFINITIONS } = await import('../../src/workflow/definitions/default-workflow');
      const checkStage = DEFAULT_STAGE_DEFINITIONS.find(s => s.stage === 'check');
      expect(checkStage).toBeDefined();

      const reviewRepairPolicy = checkStage!.repairPolicies?.find(
        p => p.fixTaskId === 'fix-review-findings',
      );
      expect(reviewRepairPolicy).toBeDefined();
      expect(reviewRepairPolicy!.inputFrom).toBeUndefined();

      const reviewFailurePolicy = checkStage!.checkFailurePolicies?.find(
        p => p.checkName === 'review-passed',
      );
      expect(reviewFailurePolicy).toBeDefined();
      expect(reviewFailurePolicy!.inputFrom).toBeUndefined();

      const reviewCheck = checkStage!.checks.find(check => check.name === 'review-passed');
      expect(reviewCheck?.onFailure?.retry?.task.with).toMatchObject({
        prompt: {
          inline: expect.stringContaining('{{ artifacts.openspecChange }}/review.md'),
        },
      });
    });
  });

  describe('Check remains read-only', () => {
    it('ReviewPassedCheck does not modify files', async () => {
      const { ReviewPassedCheck } = await import('../../src/workflow/checks/review-passed-check');

      const reviewContent = [
        '<promise>FAIL</promise>',
        '',
        '- [ID: bug-1]',
        '  Severity: blocking',
        '  Evidence: Missing guard',
        '',
        '- [ID: bug-2]',
        '  Severity: blocking',
        '  Evidence: Unused import',
      ].join('\n');
      writeArtifact(changeDir, 'review.md', reviewContent);

      const contentBefore = fs.readFileSync(path.join(changeDir, 'review.md'), 'utf-8');
      const check = new ReviewPassedCheck();
      const result = await check.run(makeCheckContext(changeDir));
      const contentAfter = fs.readFileSync(path.join(changeDir, 'review.md'), 'utf-8');

      expect(result.status).toBe('fail');
      expect(contentBefore).toBe(contentAfter);
      expect((result.output as any).structuredResult.items).toHaveLength(2);
    });
  });

  describe('Multiple blocking items passed together', () => {
    it('all blocking items from one failed review are available in reaction context', async () => {
      const { buildFailedCheckContext } = await import('../../src/workflow/model');

      const failedCheck = {
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
      };

      const ctx = buildFailedCheckContext(failedCheck);
      expect(ctx.blockingItems).toHaveLength(3);
      expect(ctx.blockingItems.map(i => i.id)).toEqual(['block-1', 'block-2', 'block-3']);

      const capturedPrompts: string[] = [];
      const mockHandler = async (input: any) => {
        capturedPrompts.push(input.prompt);
        return {
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed' as const,
          artifacts: [],
          attempts: 1,
          duration: 100,
        };
      };

      const { createRepairFixAdapter } = await import('../../src/workflow/task-runtime/repair-fix-adapter');
      const sut = createRepairFixAdapter({ agentSessionHandler: mockHandler as any });

      const mockCtx = {
        issue: makeIssue(),
        artifactManager: {
          getChangeDir: () => changeDir,
          exists: () => true,
        },
        eventBus: { emit: vi.fn() },
        acpOptions: {},
      } as any;

      await sut.dispatch('fix-review-findings', mockCtx, {
        worktreePath: changeDir,
        failedCheck,
        attempt: 1,
        failedCheckContext: ctx,
      });

      expect(capturedPrompts).toHaveLength(1);
      const prompt = capturedPrompts[0];
      expect(prompt).toContain('block-1');
      expect(prompt).toContain('block-2');
      expect(prompt).toContain('block-3');
      expect(prompt).toContain('Issue A');
      expect(prompt).toContain('Issue B');
      expect(prompt).toContain('Issue C');
      expect(prompt).toContain('Blocking Items (3)');
    });
  });
});
