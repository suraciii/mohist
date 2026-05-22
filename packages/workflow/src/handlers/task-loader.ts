import type { LoadedTaskInput } from '../domain';

export interface TaskLoadInput {
  run: unknown;
  stage: string;
  definition: { uses: string; with?: Record<string, unknown> };
}

export type TaskLoadResult =
  | { state: 'loaded'; tasks: LoadedTaskInput[] }
  | { state: 'empty' }
  | { state: 'missing'; message?: string }
  | { state: 'invalid'; message?: string };

export interface TaskLoader {
  load(input: TaskLoadInput): Promise<TaskLoadResult>;
}
