import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import { createRepairFixAdapter, type RepairFixTaskId } from '../../../src/workflow/task-runtime/repair-fix-adapter';

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

function makeContext(overrides?: Partial<StageContext>): StageContext {
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
      getChangeDir: vi.fn().mockReturnValue('/tmp/change-dir'),
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