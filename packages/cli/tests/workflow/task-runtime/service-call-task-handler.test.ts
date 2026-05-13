import { describe, it, expect, vi } from 'vitest';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import type { ServiceCallTaskInput } from '../../../src/workflow/task-runtime/types';
import { createServiceCallTaskHandler } from '../../../src/workflow/task-runtime/service-call-task-handler';

function makeContext(): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Test body',
      stage: Stage.Integrate,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: '/tmp/worktree' } as any,
    artifactManager: {} as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
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
  };
}

describe('ServiceCallTaskHandler', () => {
  it('normalizes successful service invocation with stage_task_update events', async () => {
    const handler = createServiceCallTaskHandler();
    const ctx = makeContext();
    const serviceFn = vi.fn().mockResolvedValue({ merged: true, targetBranch: 'main' });
    const input: ServiceCallTaskInput = {
      taskId: 'integrate:merge',
      title: 'Merge to main',
      serviceFn,
      stage: 'integrate',
      attempt: 1,
    };

    const result = await handler(input, ctx);

    expect(result).toMatchObject({
      taskId: 'integrate:merge',
      title: 'Merge to main',
      status: 'completed',
      attempts: 1,
      output: expect.objectContaining({
        kind: 'service-call-task',
        stage: 'integrate',
        success: true,
        result: { merged: true, targetBranch: 'main' },
      }),
    });

    expect(serviceFn).toHaveBeenCalledWith(ctx);
    expect(ctx.eventBus.emit).toHaveBeenCalledWith(
      'stage_task_update',
      expect.objectContaining({ taskId: 'integrate:merge', status: 'started' }),
    );
    expect(ctx.eventBus.emit).toHaveBeenCalledWith(
      'stage_task_update',
      expect.objectContaining({ taskId: 'integrate:merge', status: 'completed' }),
    );
  });

  it('normalizes failed service invocation with stage_task_update events', async () => {
    const handler = createServiceCallTaskHandler();
    const ctx = makeContext();
    const serviceFn = vi.fn().mockRejectedValue(new Error('Merge conflict'));
    const input: ServiceCallTaskInput = {
      taskId: 'integrate:merge',
      title: 'Merge to main',
      serviceFn,
      stage: 'integrate',
      attempt: 1,
    };

    const result = await handler(input, ctx);

    expect(result).toMatchObject({
      taskId: 'integrate:merge',
      title: 'Merge to main',
      status: 'failed',
      attempts: 1,
      output: expect.objectContaining({
        kind: 'service-call-task',
        stage: 'integrate',
        success: false,
        error: 'Merge conflict',
      }),
    });

    expect(ctx.eventBus.emit).toHaveBeenCalledWith(
      'stage_task_update',
      expect.objectContaining({ taskId: 'integrate:merge', status: 'failed' }),
    );
  });

  it('emits started then completed/failed stage_task_update events in correct order', async () => {
    const emitCalls: string[] = [];
    const ctx = makeContext();
    ctx.eventBus.emit = vi.fn().mockImplementation((event: string, data: any) => {
      if (event === 'stage_task_update') {
        emitCalls.push(data.status);
      }
    });

    const handler = createServiceCallTaskHandler();
    const serviceFn = vi.fn().mockResolvedValue({ success: true });
    const input: ServiceCallTaskInput = {
      taskId: 'integrate:spec-sync',
      title: 'Sync spec',
      serviceFn,
      stage: 'integrate',
      attempt: 1,
    };

    await handler(input, ctx);

    expect(emitCalls).toEqual(['started', 'completed']);
  });

  it('records duration for both success and failure cases', async () => {
    const handler = createServiceCallTaskHandler();
    const ctx = makeContext();

    const successfulServiceFn = vi.fn().mockResolvedValue({ ok: true });
    const input1: ServiceCallTaskInput = {
      taskId: 'integrate:spec-sync',
      title: 'Sync spec',
      serviceFn: successfulServiceFn,
      stage: 'integrate',
      attempt: 1,
    };

    const result1 = await handler(input1, ctx);
    expect(result1.duration).toBeGreaterThanOrEqual(0);

    const failingServiceFn = vi.fn().mockRejectedValue(new Error('Failed'));
    const input2: ServiceCallTaskInput = {
      taskId: 'integrate:merge',
      title: 'Merge',
      serviceFn: failingServiceFn,
      stage: 'integrate',
      attempt: 1,
    };

    const result2 = await handler(input2, ctx);
    expect(result2.duration).toBeGreaterThanOrEqual(0);
  });

  it('handler does not own checkpointing, stage transitions, or approval decisions', async () => {
    const handler = createServiceCallTaskHandler();
    const ctx = makeContext();
    const serviceFn = vi.fn().mockResolvedValue({ done: true });
    const input: ServiceCallTaskInput = {
      taskId: 'integrate:archive-change',
      title: 'Archive change',
      serviceFn,
      stage: 'integrate',
      attempt: 1,
    };

    await handler(input, ctx);

    expect(ctx.checkpointManager).toBeDefined();
    expect(ctx.emit).not.toHaveBeenCalledWith('approval_requested');
    expect(ctx.emit).not.toHaveBeenCalledWith('stage_completed');
    expect(ctx.emit).not.toHaveBeenCalledWith('stage_failed');
  });
});