import { WorkflowRun } from './model';
import { Registry } from './registry';
import { workflowDefinitionFromInput } from './workflow-definition-input';
import { WorkflowRunner } from './workflow-runner';
import type { CreateWorkflowRuntimeInput, WorkflowCreateInput, WorkflowRuntime } from './workflow-types';

export * from './workflow-types';
export type { TaskHandler, CheckHandler, TaskLoader } from './registry';

export function createWorkflowRuntime(input: CreateWorkflowRuntimeInput): WorkflowRuntime {
  const registry = new Registry();

  for (const [uses, handler] of Object.entries(input.tasks ?? {})) {
    registry.registerTask(uses, handler);
  }
  for (const [uses, handler] of Object.entries(input.checks ?? {})) {
    registry.registerCheck(uses, handler);
  }
  for (const [uses, loader] of Object.entries(input.taskLoaders ?? {})) {
    registry.registerTaskLoader(uses, loader);
  }

  return {
    async create(createInput: WorkflowCreateInput) {
      const definition = workflowDefinitionFromInput(createInput.definition);
      const run = new WorkflowRun(createInput.id, definition.stages);
      return new WorkflowRunner(run, input.store, registry);
    },

    async load(id) {
      const run = await input.store.load(id);
      if (!run) return null;
      return new WorkflowRunner(run, input.store, registry);
    },
  };
}
