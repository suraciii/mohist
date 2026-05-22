import { vi } from 'vitest';
import type { CheckHandler, CheckResult, TaskResult, WorkflowRun, WorkflowRunId, WorkflowStore } from '../../src';

export function memoryStore(): WorkflowStore & { saved: WorkflowRun[] } {
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

export function deferred<T = void>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(settle => {
    resolve = settle;
  });
  return { promise, resolve };
}

export function passingCheck(): CheckHandler {
  return {
    run: vi.fn(async input => passCheck(input.name)),
  };
}

export function completedTask(): TaskResult {
  return { status: 'completed' };
}

function passCheck(name: string): CheckResult {
  return {
    name,
    status: 'pass',
    output: { checked: name },
  };
}
