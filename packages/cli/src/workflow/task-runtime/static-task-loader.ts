import type { StageContext } from '../stage-context';
import type { TaskKind, TaskDefinition, ExecutableTask } from './types';

export interface StaticTaskResolver {
  resolvePrompt(taskId: string, ctx: StageContext): string;
}

export interface StaticTaskDefinition extends TaskDefinition {
  resolveInput?: (ctx: StageContext) => unknown;
}

export class StaticTaskLoader {
  constructor(
    private definitions: StaticTaskDefinition[],
    private resolvers: Partial<Record<TaskKind, StaticTaskResolver>>,
  ) {}

  load(ctx: StageContext): ExecutableTask[] {
    return this.definitions.map((def) => {
      const resolver = this.resolvers[def.kind];
      const prompt = resolver ? resolver.resolvePrompt(def.taskId, ctx) : undefined;
      return {
        taskId: def.taskId,
        title: def.title,
        kind: def.kind,
        prompt,
        input: def.resolveInput ? def.resolveInput(ctx) : undefined,
        artifactVerification: undefined,
      };
    });
  }
}