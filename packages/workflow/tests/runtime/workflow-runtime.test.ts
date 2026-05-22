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

describe('workflow runtime', () => {
  it('drives a workflow through caller-provided store and handlers', async () => {
    const store = memoryStore();
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => ({ status: 'completed' })),
    };
    const checkHandler: CheckHandler = {
      run: vi.fn(async input => ({
        name: input.name,
        status: 'pass',
        output: { checked: input.name },
      })),
    };
    const runtime = new WorkflowRuntime({
      store,
      tasks: { 'test/task': taskHandler },
      checks: { 'test/check': checkHandler },
    });

    const runner = await runtime.create({
      id: 'run-1',
      definition: {
        id: 'test/workflow',
        stages: [
          {
            stage: 'plan',
            tasks: [{ id: 'draft', title: 'Draft plan', uses: 'test/task', with: { step: 'plan' } }],
            checks: [{ name: 'plan-ok', title: 'Plan OK', uses: 'test/check' }],
            requiresApproval: true,
          },
          {
            stage: 'build',
            tasks: [{ id: 'implement', title: 'Implement', uses: 'test/task', with: { step: 'build' } }],
            checks: [{ name: 'build-ok', title: 'Build OK', uses: 'test/check' }],
          },
        ],
      },
    });

    await runner.start();

    expect(runner.status).toBe('running');
    expect(runner.currentStage).toBe('plan');
    expect(runner.stages[0]).toMatchObject({
      stage: 'plan',
      status: 'awaiting-approval',
      tasks: [{ id: 'draft', status: 'completed' }],
      checks: [{ name: 'plan-ok', status: 'passed', output: { checked: 'plan-ok' } }],
      approval: { status: 'awaiting' },
    });
    expect(taskHandler.run).toHaveBeenCalledWith({ id: 'draft', title: 'Draft plan', with: { step: 'plan' } });
    expect(checkHandler.run).toHaveBeenCalledWith({ name: 'plan-ok', title: 'Plan OK', with: undefined });

    const loaded = await runtime.load('run-1');
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
    expect(taskHandler.run).toHaveBeenCalledWith({ id: 'implement', title: 'Implement', with: { step: 'build' } });
    expect(store.saved.length).toBeGreaterThan(1);
  });

  it('materializes tasks through caller-provided task loaders', async () => {
    const store = memoryStore();
    const loadedTasks = [
      { id: 'generated-1', title: 'Generated task 1', uses: 'test/task', with: { item: 1 } },
      { id: 'generated-2', title: 'Generated task 2', uses: 'test/task', with: { item: 2 } },
    ];
    const loader: TaskLoader = {
      load: vi.fn(async input => {
        expect(input.stage).toBe('build');
        expect(input.definition).toEqual({ uses: 'test/load-tasks', with: { source: 'tasks.json' } });
        return { state: 'loaded', tasks: loadedTasks };
      }),
    };
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => ({ status: 'completed' })),
    };
    const checkHandler: CheckHandler = {
      run: vi.fn(async input => ({ name: input.name, status: 'pass' })),
    };
    const runtime = new WorkflowRuntime({
      store,
      taskLoaders: { 'test/load-tasks': loader },
      tasks: { 'test/task': taskHandler },
      checks: { 'test/check': checkHandler },
    });

    const runner = await runtime.create({
      id: 'run-2',
      definition: {
        id: 'test/dynamic-workflow',
        stages: [
          {
            stage: 'build',
            tasks: [],
            tasksFrom: { uses: 'test/load-tasks', with: { source: 'tasks.json' } },
            checks: [{ name: 'done', title: 'Done', uses: 'test/check' }],
          },
        ],
      },
    });

    await runner.start();

    expect(runner.status).toBe('completed');
    expect(loader.load).toHaveBeenCalledTimes(1);
    expect(taskHandler.run).toHaveBeenCalledTimes(2);
    expect(taskHandler.run).toHaveBeenNthCalledWith(1, { id: 'generated-1', title: 'Generated task 1', with: { item: 1 } });
    expect(taskHandler.run).toHaveBeenNthCalledWith(2, { id: 'generated-2', title: 'Generated task 2', with: { item: 2 } });
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
});
