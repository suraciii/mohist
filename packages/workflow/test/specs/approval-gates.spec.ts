import { describe, expect, it, vi } from 'vitest';
import { type TaskHandler, WorkflowRuntime } from '../../src';
import { completedTask, memoryStore, passingCheck } from '../utils';

describe('approval gates', () => {
  it('given a workflow with an approval gate, when started, then it runs to awaiting approval', async () => {
    const store = memoryStore();
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => completedTask()),
    };
    const checkHandler = passingCheck();
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/task': taskHandler },
      checks: { 'spec/check': checkHandler },
    });
    const runner = await runtime.create({
      id: 'approval-run',
      definition: {
        id: 'spec/approval-workflow',
        stages: [
          {
            stage: 'plan',
            tasks: [{ id: 'draft', title: 'Draft plan', uses: 'spec/task', with: { stage: 'plan' } }],
            checks: [{ name: 'plan-ok', title: 'Plan OK', uses: 'spec/check' }],
            requiresApproval: true,
          },
          {
            stage: 'build',
            tasks: [{ id: 'implement', title: 'Implement', uses: 'spec/task', with: { stage: 'build' } }],
            checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
          },
        ],
      },
    });

    await runner.run();

    expect(runner.status).toBe('running');
    expect(runner.currentStage).toBe('plan');
    expect(runner.stages).toMatchObject([
      {
        stage: 'plan',
        status: 'awaiting-approval',
        tasks: [{ id: 'draft', status: 'completed' }],
        checks: [{ name: 'plan-ok', status: 'passed', output: { checked: 'plan-ok' } }],
        approval: { status: 'awaiting' },
      },
      {
        stage: 'build',
        status: 'pending',
        tasks: [],
        checks: [],
      },
    ]);
    expect(taskHandler.run).toHaveBeenCalledTimes(1);
    expect(taskHandler.run).toHaveBeenCalledWith({ id: 'draft', title: 'Draft plan', with: { stage: 'plan' } });
    expect(checkHandler.run).toHaveBeenCalledTimes(1);
    expect(checkHandler.run).toHaveBeenCalledWith({ name: 'plan-ok', title: 'Plan OK', with: undefined });
    expect(store.saved.length).toBeGreaterThan(1);
  });

  it('given a run awaiting approval, when approved, then it continues to completion', async () => {
    const store = memoryStore();
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => completedTask()),
    };
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/task': taskHandler },
      checks: { 'spec/check': passingCheck() },
    });
    const runner = await runtime.create({
      id: 'approve-run',
      definition: {
        id: 'spec/approval-workflow',
        stages: [
          {
            stage: 'plan',
            tasks: [{ id: 'draft', title: 'Draft plan', uses: 'spec/task' }],
            checks: [{ name: 'plan-ok', title: 'Plan OK', uses: 'spec/check' }],
            requiresApproval: true,
          },
          {
            stage: 'build',
            tasks: [{ id: 'implement', title: 'Implement', uses: 'spec/task' }],
            checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
          },
        ],
      },
    });
    await runner.run();

    const loaded = await runtime.load('approve-run');
    expect(loaded).not.toBeNull();
    await loaded?.approve();
    await loaded?.nextYield();

    expect(loaded?.status).toBe('completed');
    expect(loaded?.currentStage).toBe('build');
    expect(loaded?.stages).toMatchObject([
      { stage: 'plan', status: 'passed', approval: { status: 'approved' } },
      {
        stage: 'build',
        status: 'passed',
        tasks: [{ id: 'implement', status: 'completed' }],
        checks: [{ name: 'build-ok', status: 'passed' }],
      },
    ]);
    expect(taskHandler.run).toHaveBeenCalledTimes(2);
  });
});
