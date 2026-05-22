import { describe, expect, it, vi } from 'vitest';
import {
  type CheckHandler,
  type TaskHandler,
  type TaskLoader,
  type WorkflowRun,
  type WorkflowRunId,
  WorkflowRuntime,
  type WorkflowStore,
} from '../../src';

function memoryStore(): WorkflowStore & { saved: WorkflowRun[] } {
  const runs = new Map<WorkflowRunId, WorkflowRun>();
  const saved: WorkflowRun[] = [];
  return {
    saved,
    async load(id) {
      return runs.get(id) ?? null;
    },
    async save(run) {
      runs.set(run.id, run);
      saved.push(run);
    },
  };
}

function deferred<T = void>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(settle => {
    resolve = settle;
  });
  return { promise, resolve };
}

function passingCheck(): CheckHandler {
  return {
    run: vi.fn(async input => ({
      name: input.name,
      status: 'pass',
      output: { checked: input.name },
    })),
  };
}

describe('workflow runtime specs', () => {
  it('given a workflow with an approval gate, when started, then it runs to awaiting approval', async () => {
    const store = memoryStore();
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => ({ status: 'completed' })),
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

    await runner.start();

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
      run: vi.fn(async () => ({ status: 'completed' })),
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
    await runner.start();

    const loaded = await runtime.load('approve-run');
    expect(loaded).not.toBeNull();
    await loaded?.approve();

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

  it('given a tasksFrom source, when started, then loader materializes tasks before checks', async () => {
    const store = memoryStore();
    const loader: TaskLoader = {
      load: vi.fn(async input => {
        expect(input.stage).toBe('build');
        expect(input.definition).toEqual({ uses: 'spec/load-tasks', with: { source: 'tasks.json' } });
        return {
          state: 'loaded',
          tasks: [
            { id: 'generated-1', title: 'Generated task 1', uses: 'spec/task', with: { item: 1 } },
            { id: 'generated-2', title: 'Generated task 2', uses: 'spec/task', with: { item: 2 } },
          ],
        };
      }),
    };
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => ({ status: 'completed' })),
    };
    const checkHandler = passingCheck();
    const runtime = new WorkflowRuntime({
      store,
      taskLoaders: { 'spec/load-tasks': loader },
      tasks: { 'spec/task': taskHandler },
      checks: { 'spec/check': checkHandler },
    });
    const runner = await runtime.create({
      id: 'dynamic-run',
      definition: {
        id: 'spec/dynamic-workflow',
        stages: [
          {
            stage: 'build',
            tasks: [],
            tasksFrom: { uses: 'spec/load-tasks', with: { source: 'tasks.json' } },
            checks: [{ name: 'done', title: 'Done', uses: 'spec/check' }],
          },
        ],
      },
    });

    await runner.start();

    expect(runner.status).toBe('completed');
    expect(loader.load).toHaveBeenCalledTimes(1);
    expect(taskHandler.run).toHaveBeenNthCalledWith(1, { id: 'generated-1', title: 'Generated task 1', with: { item: 1 } });
    expect(taskHandler.run).toHaveBeenNthCalledWith(2, { id: 'generated-2', title: 'Generated task 2', with: { item: 2 } });
    expect(checkHandler.run).toHaveBeenCalledWith({ name: 'done', title: 'Done', with: undefined });
    expect(runner.stages[0]).toMatchObject({
      stage: 'build',
      status: 'passed',
      tasks: [
        { id: 'generated-1', status: 'completed' },
        { id: 'generated-2', status: 'completed' },
      ],
      checks: [{ name: 'done', status: 'passed' }],
    });
  });

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
      run: vi.fn(async () => ({ status: 'completed' })),
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
      run: vi.fn(async () => ({ status: 'completed' })),
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
