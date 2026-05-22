import { WorkflowRun } from '../domain';
import { parseWorkflowDefinition } from '../definition';
import { Registry } from './registry';
import { WorkflowRunner } from './runner';
import type { WorkflowCreateInput, WorkflowRunId, WorkflowRuntimeOptions, WorkflowStore } from './types';

export class WorkflowRuntime {
  private readonly registry = new Registry();

  constructor(private readonly options: WorkflowRuntimeOptions) {
    for (const [uses, handler] of Object.entries(options.tasks ?? {})) {
      this.registry.registerTask(uses, handler);
    }
    for (const [uses, handler] of Object.entries(options.checks ?? {})) {
      this.registry.registerCheck(uses, handler);
    }
    for (const [uses, loader] of Object.entries(options.taskLoaders ?? {})) {
      this.registry.registerTaskLoader(uses, loader);
    }
  }

  async create(input: WorkflowCreateInput): Promise<WorkflowRunner> {
    const definition = parseWorkflowDefinition(input.definition);
    const run = new WorkflowRun(input.id, definition.stages);
    return new WorkflowRunner(run, this.store, this.registry);
  }

  async load(id: WorkflowRunId): Promise<WorkflowRunner | null> {
    const run = await this.store.load(id);
    if (!run) return null;
    return new WorkflowRunner(run, this.store, this.registry);
  }

  private get store(): WorkflowStore {
    return this.options.store;
  }
}
