import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import { createRepairFixAdapter, type RepairFixTaskId } from '../../../src/workflow/task-runtime/repair-fix-adapter';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const { executeMock, closeMock, createMock } = vi.hoisted(() => ({
  executeMock: vi.fn(),
  closeMock: vi.fn(),
  createMock: vi.fn(),
}));

vi.mock('../../../src/agent-runtime', () => ({
  AgentSession: {
    create: createMock,
  },
  createWorkflowSessionObservers: vi.fn().mockReturnValue([]),
}));

function makeContext(overrides?: Partial<StageContext> & { changeDir?: string }): StageContext {
  const changeDir = overrides?.changeDir ?? '/tmp/change-dir';
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Test body',
      stage: Stage.Check,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: '/tmp/worktree' } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
      exists: vi.fn().mockReturnValue(true),
    } as any,
    worktreeManager: {
      getPath: vi.fn().mockReturnValue('/tmp/worktree/project-1/159'),
      getHeadSha: vi.fn().mockResolvedValue('abc123'),
      rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
    } as any,
    projectRepo: {
      findById: vi.fn().mockReturnValue({
        id: 'project-1',
        name: 'project-1',
        path: '/tmp/project',
        baseBranch: 'main',
      }),
    } as any,
    eventBus: { emit: vi.fn() } as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
    workflowLogRepo: undefined,
    sessionStreamLogRepo: undefined,
    coderSessionRepo: undefined,
    stageExecutionRepo: undefined,
    checkSuiteRepo: undefined,
    stageStateService: undefined,
    workflowRunService: undefined,
    workflowApplicationService: undefined,
    workflowRun: undefined,
    requestedWork: undefined,
    requestedTask: undefined,
    signal: undefined,
    emit: vi.fn(),
    log: vi.fn(),
    ...overrides,
  } as unknown as StageContext;
}

function makeChangeDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-reaction-task-'));
  fs.mkdirSync(path.join(dir, 'specs', 'workflow'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'proposal.md'), '# Proposal');
  fs.writeFileSync(path.join(dir, 'design.md'), '# Design');
  fs.writeFileSync(path.join(dir, 'specs', 'workflow', 'spec.md'), '# Spec');
  fs.writeFileSync(path.join(dir, 'tasks.json'), '{"tasks":[]}');
  fs.writeFileSync(path.join(dir, 'review.md'), '# Review');
  return dir;
}

describe('RepairFixAdapter', () => {
  beforeEach(() => {
    executeMock.mockReset();
    closeMock.mockReset();
    createMock.mockReset();
    createMock.mockResolvedValue({
      execute: executeMock,
      close: closeMock,
    });
    closeMock.mockResolvedValue(undefined);
  });

  describe('health fix dispatch', () => {
    const healthFixTaskIds: RepairFixTaskId[] = [
      'fix-plan-health',
      'fix-build-health',
      'fix-check-health',
      'fix-integrate-health',
    ];

    for (const taskId of healthFixTaskIds) {
      it(`dispatches ${taskId} through agent session handler`, async () => {
        executeMock.mockResolvedValue({
          success: true,
          text: 'fixed',
          acpSessionId: 'ses-health-fix',
        });

        const adapter = createRepairFixAdapter();
        const ctx = makeContext();
        const context = {
          worktreePath: '/tmp/worktree',
          failedCheck: {
            name: `health:${taskId.replace('fix-', '').replace('-health', '')}`,
            status: 'fail' as const,
            message: 'Health check failed',
            output: { logExcerpt: 'error' },
          },
          attempt: 1,
        };

        const result = await adapter.dispatch(taskId, ctx, context);

        expect(result.taskId).toBe(taskId);
        expect(result.status).toBe('completed');
        expect(createMock).toHaveBeenCalled();
        expect(executeMock).toHaveBeenCalled();
      });
    }

    it('builds health fix prompts with issue and OpenSpec context', async () => {
      const changeDir = makeChangeDir();
      executeMock.mockResolvedValue({
        success: true,
        text: 'fixed',
        acpSessionId: 'ses-health-fix',
      });

      try {
        const adapter = createRepairFixAdapter();
        const ctx = makeContext({ changeDir });

        await adapter.dispatch('fix-build-health', ctx, {
          worktreePath: '/tmp/worktree',
          failedCheck: {
            name: 'health:build',
            status: 'fail',
            message: 'npm run build failed',
            output: { command: 'npm run build', logExcerpt: 'TypeScript error' },
          },
          attempt: 1,
        });

        const prompt = executeMock.mock.calls[0][0] as string;
        expect(prompt).toContain('<mohist-task>');
        expect(prompt).toContain('Issue #159: Test Issue');
        expect(prompt).toContain('Test body');
        expect(prompt).toContain(`@${path.join(changeDir, 'proposal.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'design.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'tasks.json')}`);
        expect(prompt).toContain('Health command: npm run build');
      } finally {
        fs.rmSync(changeDir, { recursive: true, force: true });
      }
    });
  });

  describe('agent session repair dispatch', () => {
    it('dispatches repair-plan-artifacts through agent session handler', async () => {
      executeMock.mockResolvedValue({
        success: true,
        text: 'repaired',
        acpSessionId: 'ses-plan-repair',
      });

      const adapter = createRepairFixAdapter();
      const baseCtx = makeContext();
      const ctx = makeContext({ issue: { ...baseCtx.issue, stage: Stage.Plan } });
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'plan-artifacts-complete',
          status: 'fail' as const,
          message: 'Missing proposal.md',
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('repair-plan-artifacts', ctx, context);

      expect(result.taskId).toBe('repair-plan-artifacts');
      expect(result.status).toBe('completed');
      expect(createMock).toHaveBeenCalled();
    });

    it('builds plan reaction prompts with issue and OpenSpec context', async () => {
      const changeDir = makeChangeDir();
      executeMock.mockResolvedValue({
        success: true,
        text: 'repaired',
        acpSessionId: 'ses-plan-reaction',
      });

      try {
        const adapter = createRepairFixAdapter();
        const baseCtx = makeContext({ changeDir });
        const ctx = makeContext({
          changeDir,
          issue: {
            ...baseCtx.issue,
            number: 199,
            title: 'Unify session context',
            body: 'Every agent session must receive issue and OpenSpec context.',
            stage: Stage.Plan,
          },
        });

        await adapter.dispatch('repair-plan-artifacts', ctx, {
          worktreePath: '/tmp/worktree',
          failedCheck: {
            name: 'self-review-passed',
            status: 'fail',
            message: 'self-review reported missing requirements',
          },
          attempt: 1,
        });

        const prompt = executeMock.mock.calls[0][0] as string;
        expect(prompt).toContain('Issue #199: Unify session context');
        expect(prompt).toContain('Every agent session must receive issue and OpenSpec context.');
        expect(prompt).toContain(`@${path.join(changeDir, 'proposal.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'design.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'specs', 'workflow', 'spec.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'tasks.json')}`);
      } finally {
        fs.rmSync(changeDir, { recursive: true, force: true });
      }
    });

    it('dispatches fix-review-findings through agent session handler', async () => {
      executeMock.mockResolvedValue({
        success: true,
        text: 'fixed',
        acpSessionId: 'ses-review-fix',
      });

      const adapter = createRepairFixAdapter();
      const ctx = makeContext();
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'review-passed',
          status: 'fail' as const,
          message: 'AI review returned FAIL verdict',
          output: {
            verdict: 'FAIL',
            reviewReport: 'Review report content',
            fixSuggestions: 'Fix suggestion content',
          },
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('fix-review-findings', ctx, context);

      expect(result.taskId).toBe('fix-review-findings');
      expect(result.status).toBe('completed');
      expect(createMock).toHaveBeenCalled();
    });

    it('builds review reaction prompts with issue and OpenSpec context', async () => {
      const changeDir = makeChangeDir();
      executeMock.mockResolvedValue({
        success: true,
        text: 'fixed',
        acpSessionId: 'ses-review-reaction',
      });

      try {
        const adapter = createRepairFixAdapter();
        const baseCtx = makeContext({ changeDir });
        const ctx = makeContext({
          changeDir,
          issue: {
            ...baseCtx.issue,
            number: 199,
            title: 'Unify session context',
            body: 'Every agent session must receive issue and OpenSpec context.',
            stage: Stage.Check,
          },
        });

        await adapter.dispatch('fix-review-findings', ctx, {
          worktreePath: '/tmp/worktree',
          failedCheck: {
            name: 'review-passed',
            status: 'fail',
            output: {
              verdict: 'FAIL',
              reviewReport: 'Missing spec coverage.',
              fixSuggestions: 'Update the workflow runner.',
            },
          },
          attempt: 1,
        });

        const prompt = executeMock.mock.calls[0][0] as string;
        expect(prompt).toContain('Issue #199: Unify session context');
        expect(prompt).toContain('Every agent session must receive issue and OpenSpec context.');
        expect(prompt).toContain(`@${path.join(changeDir, 'proposal.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'design.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'specs', 'workflow', 'spec.md')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'tasks.json')}`);
        expect(prompt).toContain(`@${path.join(changeDir, 'review.md')}`);
      } finally {
        fs.rmSync(changeDir, { recursive: true, force: true });
      }
    });
  });

  describe('merge repair dispatch', () => {
    it('dispatches repair-merge through service call handler', async () => {
      const adapter = createRepairFixAdapter();
      const ctx = makeContext();
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'merge-ready',
          status: 'fail' as const,
          message: 'Merge conflict detected',
          output: {
            targetBranch: 'main',
            conflictFiles: ['src/file.ts'],
          },
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('repair-merge', ctx, context);

      expect(result.taskId).toBe('repair-merge');
      expect(result.status).toBe('completed');
      expect(result.output).toMatchObject({
        kind: 'merge-repair',
        success: true,
        headChanged: false,
      });
    });

    it('returns failed when project not found for merge repair', async () => {
      const adapter = createRepairFixAdapter();
      const ctx = makeContext({
        projectRepo: { findById: vi.fn().mockReturnValue(null) } as any,
      });
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'merge-ready',
          status: 'fail' as const,
          message: 'Merge conflict',
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('repair-merge', ctx, context);

      expect(result.taskId).toBe('repair-merge');
      expect(result.status).toBe('failed');
      expect(result.output).toMatchObject({
        kind: 'merge-repair',
        success: false,
        error: 'Project not found',
      });
    });

    it('returns failed when worktree not found for merge repair', async () => {
      const adapter = createRepairFixAdapter();
      const ctx = makeContext({
        worktreeManager: {
          getPath: vi.fn().mockReturnValue(null),
        } as any,
      });
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'merge-ready',
          status: 'fail' as const,
          message: 'Merge conflict',
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('repair-merge', ctx, context);

      expect(result.taskId).toBe('repair-merge');
      expect(result.status).toBe('failed');
      expect(result.output).toMatchObject({
        kind: 'merge-repair',
        success: false,
        error: 'Worktree not found',
      });
    });

    it('reports head changed when rebase modifies HEAD', async () => {
      const adapter = createRepairFixAdapter();
      const ctx = makeContext();
      ctx.worktreeManager = {
        ...ctx.worktreeManager,
        getHeadSha: vi.fn()
          .mockResolvedValueOnce('abc123')
          .mockResolvedValueOnce('def456'),
        rebaseOntoMaster: vi.fn().mockResolvedValue({ success: true, conflicts: [] }),
      } as any;
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'merge-ready',
          status: 'fail' as const,
          message: 'Merge conflict',
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('repair-merge', ctx, context);

      expect(result.output).toMatchObject({
        kind: 'merge-repair',
        success: true,
        headChanged: true,
        headBefore: 'abc123',
        headAfter: 'def456',
      });
    });

    it('returns failed result when rebaseOntoMaster returns success:false', async () => {
      const adapter = createRepairFixAdapter();
      const ctx = makeContext();
      ctx.worktreeManager = {
        ...ctx.worktreeManager,
        rebaseOntoMaster: vi.fn().mockResolvedValue({ success: false, conflicts: ['src/conflict.ts'] }),
      } as any;
      const context = {
        worktreePath: '/tmp/worktree',
        failedCheck: {
          name: 'merge-ready',
          status: 'fail' as const,
          message: 'Merge conflict',
        },
        attempt: 1,
      };

      const result = await adapter.dispatch('repair-merge', ctx, context);

      expect(result.status).toBe('failed');
      expect(result.output).toMatchObject({
        kind: 'merge-repair',
        success: false,
        conflicts: ['src/conflict.ts'],
      });
    });
  });

  describe('adapter dispatch throws on unknown task id', () => {
    it('throws error for unknown repair/fix task id', async () => {
      const adapter = createRepairFixAdapter();
      const ctx = makeContext();

      await expect(
        adapter.dispatch('unknown-task' as RepairFixTaskId, ctx, {
          worktreePath: '/tmp/worktree',
          failedCheck: { name: 'test', status: 'fail' as const },
          attempt: 1,
        }),
      ).rejects.toThrow('Unknown repair/fix task id: unknown-task');
    });
  });

  describe('compatibility exports', () => {
    it('defaultRepairFixAdapter is available from the module', async () => {
      const { defaultRepairFixAdapter } = await import('../../../src/workflow/task-runtime');
      expect(defaultRepairFixAdapter).toBeDefined();
      expect(typeof defaultRepairFixAdapter.dispatch).toBe('function');
    });

    it('RepairFixTaskId type is exported', async () => {
      const { RepairFixTaskId } = await import('../../../src/workflow/task-runtime');
      const taskId: RepairFixTaskId = 'fix-build-health';
      expect(taskId).toBe('fix-build-health');
    });
  });
});
