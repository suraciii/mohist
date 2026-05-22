import { describe, expect, it, vi } from 'vitest';
import { type CheckHandler, type TaskHandler, type TaskLoader, WorkflowRuntime } from '../../src';
import { completedTask, memoryStore } from '../utils';

const makePassCheck = (): CheckHandler => ({
  run: vi.fn(async input => ({ name: input.name, status: 'pass' as const })),
});

describe('retry', () => {
  it('given a failed task, when retry, then the failed task is reset and re-executed', async () => {
    const store = memoryStore();
    let callCount = 0;
    const flakyTask: TaskHandler = {
      run: vi.fn(async () => {
        callCount++;
        return callCount === 1
          ? { status: 'failed' as const, reason: 'flaky' }
          : { status: 'completed' as const };
      }),
    };
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/flaky': flakyTask },
      checks: { 'spec/check': makePassCheck() },
    });
    const runner = await runtime.create({
      id: 'retry-task',
      definition: {
        id: 'spec/retry-task-workflow',
        stages: [{
          stage: 'build',
          tasks: [{ id: 'compile', title: 'Compile', uses: 'spec/flaky' }],
          checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
        }],
      },
    });

    await runner.run();
    expect(runner.status).toBe('failed');
    expect(runner.failure?.reason).toBe('task-failed');

    await runner.retry();
    await runner.nextYield();

    expect(runner.status).toBe('completed');
    expect(flakyTask.run).toHaveBeenCalledTimes(2);
  });

  it('given a failed check, when retry, then all failed checks are reset and re-executed', async () => {
    const store = memoryStore();
    let checkCallCount = 0;
    const flakyCheck: CheckHandler = {
      run: vi.fn(async input => {
        checkCallCount++;
        return checkCallCount === 1
          ? { name: input.name, status: 'fail' as const, message: 'flaky check' }
          : { name: input.name, status: 'pass' as const };
      }),
    };
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/ok': { run: vi.fn(async () => completedTask()) } },
      checks: { 'spec/flaky-check': flakyCheck },
    });
    const runner = await runtime.create({
      id: 'retry-check',
      definition: {
        id: 'spec/retry-check-workflow',
        stages: [{
          stage: 'build',
          tasks: [{ id: 'build', title: 'Build', uses: 'spec/ok' }],
          checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/flaky-check' }],
        }],
      },
    });

    await runner.run();
    expect(runner.status).toBe('failed');
    expect(runner.failure?.reason).toBe('check-unrepaired');

    await runner.retry();
    await runner.nextYield();

    expect(runner.status).toBe('completed');
    expect(flakyCheck.run).toHaveBeenCalledTimes(2);
  });

  it('given a non-failed workflow, when retry, then it throws', async () => {
    const store = memoryStore();
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/ok': { run: vi.fn(async () => completedTask()) } },
      checks: { 'spec/check': makePassCheck() },
    });
    const runner = await runtime.create({
      id: 'retry-not-failed',
      definition: {
        id: 'spec/retry-not-failed-workflow',
        stages: [{
          stage: 'build',
          tasks: [{ id: 'build', title: 'Build', uses: 'spec/ok' }],
          checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
        }],
      },
    });

    await runner.run();
    expect(runner.status).toBe('completed');

    await expect(runner.retry()).rejects.toThrow('retry requires failed');
  });
});

describe('rerun', () => {
  it('given a failed stage, when rerun, then the stage is re-initialized from scratch', async () => {
    const store = memoryStore();
    let callCount = 0;
    const flakyTask: TaskHandler = {
      run: vi.fn(async () => {
        callCount++;
        return callCount <= 2
          ? { status: 'failed' as const, reason: 'flaky' }
          : { status: 'completed' as const };
      }),
    };
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/flaky': flakyTask },
      checks: { 'spec/check': makePassCheck() },
    });
    const runner = await runtime.create({
      id: 'rerun-stage',
      definition: {
        id: 'spec/rerun-workflow',
        stages: [{
          stage: 'build',
          tasks: [{ id: 'compile', title: 'Compile', uses: 'spec/flaky' }],
          checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
        }],
      },
    });

    await runner.run();
    expect(runner.status).toBe('failed');

    await runner.rerun();
    await runner.nextYield();
    expect(runner.status).toBe('failed');

    await runner.rerun();
    await runner.nextYield();
    expect(runner.status).toBe('completed');

    expect(flakyTask.run).toHaveBeenCalledTimes(3);
  });

  it('given a stage with tasks from loader, when rerun, then the loader is called again', async () => {
    const store = memoryStore();
    let loadCount = 0;
    const loader: TaskLoader = {
      load: vi.fn(async () => {
        loadCount++;
        return loadCount === 1
          ? { state: 'loaded' as const, tasks: [{ id: 'loaded-1', title: 'Loaded 1', uses: 'spec/ok' }] }
          : { state: 'loaded' as const, tasks: [{ id: 'loaded-2', title: 'Loaded 2', uses: 'spec/ok' }] };
      }),
    };
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'spec/ok': { run: vi.fn(async () => completedTask()) } },
      checks: { 'spec/check': makePassCheck() },
      taskLoaders: { 'spec/loader': loader },
    });
    const runner = await runtime.create({
      id: 'rerun-loader',
      definition: {
        id: 'spec/rerun-loader-workflow',
        stages: [{
          stage: 'build',
          tasks: [],
          tasksFrom: { uses: 'spec/loader' },
          checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
        }],
      },
    });

    await runner.run();
    expect(runner.status).toBe('completed');
    expect(runner.stages[0].tasks.map(t => t.id)).toEqual(['loaded-1']);

    await runner.rerun();
    await runner.nextYield();
    expect(runner.status).toBe('completed');
    expect(runner.stages[0].tasks.map(t => t.id)).toEqual(['loaded-2']);
    expect(loadCount).toBe(2);
  });
});
