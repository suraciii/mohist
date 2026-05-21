import { describe, expect, it } from 'vitest';
import {
  createTaskLoaderRegistry,
  type ExecutableTask,
  type StageContext,
  type TaskLoader,
} from '../../src';

function loader(kind: TaskLoader['kind'], tasks: ExecutableTask[]): TaskLoader {
  return {
    kind,
    load: () => tasks,
  };
}

describe('task loader registry', () => {
  it('registers task loaders by work source kind', () => {
    const staticTask: ExecutableTask = {
      taskId: 'proposal',
      title: 'Proposal',
      uses: 'custom/agent',
      run: async () => ({
        taskId: 'proposal',
        title: 'Proposal',
        status: 'completed',
        artifacts: [],
        attempts: 1,
        duration: 1,
      }),
    };
    const registry = createTaskLoaderRegistry([
      loader('static', [staticTask]),
      loader('runtime', []),
    ]);

    expect(registry.get('static')?.load({} as StageContext)).toEqual([staticTask]);
    expect(registry.get('openspec')).toBeUndefined();
    expect(registry.list().map(entry => entry.kind)).toEqual(['static', 'runtime']);
  });
});
