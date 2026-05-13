import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { StageContext, StageTaskResult } from '../../../src/workflow/stage-context';
import type { TaskKind, TaskHandler, ExecutableTask } from '../../../src/workflow/task-runtime/types';
import { createTaskHandlerRegistry } from '../../../src/workflow/task-runtime/types';
import { Stage, IssueStatus } from '../../../src/types';

function makeContext(): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Test body',
      stage: Stage.Plan,
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

describe('TaskHandlerRegistry', () => {
  it('returns registered handler for known kind', async () => {
    const handler: TaskHandler = vi.fn().mockResolvedValue({
      taskId: 'test',
      title: 'Test',
      status: 'completed',
      artifacts: [],
      attempts: 1,
      duration: 100,
    } as StageTaskResult);

    const registry = createTaskHandlerRegistry({ 'agent-session': handler });
    const retrieved = registry.get('agent-session');
    expect(retrieved).toBe(handler);
  });

  it('returns undefined for unknown kind', () => {
    const registry = createTaskHandlerRegistry({});
    expect(registry.get('agent-session')).toBeUndefined();
    expect(registry.get('service-call')).toBeUndefined();
  });

  it('allows registering additional handlers', () => {
    const registry = createTaskHandlerRegistry({});
    const handler: TaskHandler = vi.fn();
    registry.register('agent-session', handler);
    expect(registry.get('agent-session')).toBe(handler);
  });

  it('execute does not own checkpointing, stage transitions, or approval decisions', async () => {
    const handler: TaskHandler = vi.fn().mockResolvedValue({
      taskId: 'test',
      title: 'Test',
      status: 'completed',
      artifacts: [],
      attempts: 1,
      duration: 100,
    } as StageTaskResult);

    const registry = createTaskHandlerRegistry({ 'agent-session': handler });
    const ctx = makeContext();
    const task: ExecutableTask = {
      taskId: 'test-task',
      title: 'Test Task',
      kind: 'agent-session',
      prompt: 'Do something',
    };

    await registry.get('agent-session')!(task, ctx);

    expect(ctx.checkpointManager).toBeDefined();
    expect(ctx.emit).not.toHaveBeenCalledWith('approval_requested');
    expect(ctx.emit).not.toHaveBeenCalledWith('stage_completed');
    expect(ctx.emit).not.toHaveBeenCalledWith('stage_failed');
  });
});