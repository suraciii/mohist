import { describe, expect, it, vi } from 'vitest';
import { type TaskHandler, WorkflowRuntime } from '../../src';
import { completedTask, deferred, memoryStore, passingCheck } from '../utils';

describe('pausing work', () => {
  it('given a running task, when pause is requested, then the workflow pauses before the next task', async () => {
    const store = memoryStore();
    const taskStarted = deferred();
    const finishTask = deferred<{ status: 'completed' }>();
    const slowTask: TaskHandler = {
      run: vi.fn(async () => {
        taskStarted.resolve();
        return finishTask.promise;
      }),
    };
    const nextTask: TaskHandler = {
      run: vi.fn(async () => completedTask()),
    };
    const checkHandler = passingCheck();
    const runtime = new WorkflowRuntime({
      store,
      tasks: {
        'spec/slow-task': slowTask,
        'spec/next-task': nextTask,
      },
      checks: { 'spec/check': checkHandler },
    });
    const runner = await runtime.create({
      id: 'pause-run',
      definition: {
        id: 'spec/pause-workflow',
        stages: [
          {
            stage: 'build',
            tasks: [
              { id: 'compile', title: 'Compile', uses: 'spec/slow-task' },
              { id: 'test', title: 'Test', uses: 'spec/next-task' },
            ],
            checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
          },
        ],
      },
    });

    const start = runner.start();
    await taskStarted.promise;
    await runner.pause('user requested pause');
    finishTask.resolve({ status: 'completed' });
    await start;

    expect(runner.status).toBe('paused');
    expect(slowTask.run).toHaveBeenCalledTimes(1);
    expect(nextTask.run).not.toHaveBeenCalled();
    expect(checkHandler.run).not.toHaveBeenCalled();
    expect(runner.stages[0]).toMatchObject({
      stage: 'build',
      status: 'running',
      tasks: [
        { id: 'compile', status: 'completed' },
        { id: 'test', status: 'pending' },
      ],
      checks: [{ name: 'build-ok', status: 'pending' }],
    });
  });

  it('given a paused workflow, when resumed, then it continues from pending work', async () => {
    const store = memoryStore();
    const taskStarted = deferred();
    const finishTask = deferred<{ status: 'completed' }>();
    const slowTask: TaskHandler = {
      run: vi.fn(async () => {
        taskStarted.resolve();
        return finishTask.promise;
      }),
    };
    const nextTask: TaskHandler = {
      run: vi.fn(async () => completedTask()),
    };
    const checkHandler = passingCheck();
    const runtime = new WorkflowRuntime({
      store,
      tasks: {
        'spec/slow-task': slowTask,
        'spec/next-task': nextTask,
      },
      checks: { 'spec/check': checkHandler },
    });
    const runner = await runtime.create({
      id: 'resume-run',
      definition: {
        id: 'spec/resume-workflow',
        stages: [
          {
            stage: 'build',
            tasks: [
              { id: 'compile', title: 'Compile', uses: 'spec/slow-task' },
              { id: 'test', title: 'Test', uses: 'spec/next-task' },
            ],
            checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
          },
        ],
      },
    });
    const start = runner.start();
    await taskStarted.promise;
    await runner.pause('user requested pause');
    finishTask.resolve({ status: 'completed' });
    await start;

    const loaded = await runtime.load('resume-run');
    expect(loaded).not.toBeNull();
    await loaded?.resume();

    expect(loaded?.status).toBe('completed');
    expect(slowTask.run).toHaveBeenCalledTimes(1);
    expect(nextTask.run).toHaveBeenCalledTimes(1);
    expect(checkHandler.run).toHaveBeenCalledTimes(1);
    expect(loaded?.stages[0]).toMatchObject({
      stage: 'build',
      status: 'passed',
      tasks: [
        { id: 'compile', status: 'completed' },
        { id: 'test', status: 'completed' },
      ],
      checks: [{ name: 'build-ok', status: 'passed' }],
    });
  });
});
