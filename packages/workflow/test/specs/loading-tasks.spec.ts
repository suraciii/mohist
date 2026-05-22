import { describe, expect, it, vi } from 'vitest';
import { type TaskHandler, type TaskLoadResult, type TaskLoader, WorkflowRuntime } from '../../src';
import { completedTask, memoryStore, passingCheck } from '../support/workflow';

describe('loading tasks', () => {
  it('given a tasksFrom source, when started, then loader adds tasks before checks', async () => {
    const store = memoryStore();
    const loader: TaskLoader = {
      load: vi.fn(async input => {
        expect(input.stage).toBe('build');
        expect(input.definition).toEqual({ uses: 'spec/load-tasks', with: { source: 'tasks.json' } });
        const result: TaskLoadResult = {
          state: 'loaded',
          tasks: [
            { id: 'generated-1', title: 'Generated task 1', uses: 'spec/task', with: { item: 1 } },
            { id: 'generated-2', title: 'Generated task 2', uses: 'spec/task', with: { item: 2 } },
          ],
        };
        return result;
      }),
    };
    const taskHandler: TaskHandler = {
      run: vi.fn(async () => completedTask()),
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
});
