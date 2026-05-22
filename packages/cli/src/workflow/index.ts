// New workflow runtime integration (replaces old WorkflowEngine)
export {
  WorkflowRuntime,
  WorkflowRunner,
} from '@mohist/workflow';

export {
  WorkflowStoreAdapter,
} from './runtime/store';

export {
  MOHIST_DEFAULT_WORKFLOW_DEFINITION,
} from './runtime/definition';

export {
  createMohistTaskHandlers,
  createMohistCheckHandlers,
  createMohistTaskLoaders,
} from './runtime/handlers';

// Re-export types from new workflow package
export type {
  WorkflowRuntimeOptions,
  WorkflowCreateInput,
  WorkflowRunId,
  WorkflowRunStatus,
  WorkflowStageState,
  WorkflowFailure,
  WorkflowStore,
  TaskHandler,
  CheckHandler,
  TaskLoader,
  WorkflowTaskInput,
  WorkflowCheckInput,
  TaskLoadInput,
  TaskLoadResult,
  TaskResult,
  CheckResult,
} from '@mohist/workflow';

// Legacy exports kept for compatibility (TODO: migrate all usages, then delete)
export {
  Stage,
  STAGE_TRANSITIONS,
  isValidTransition,
} from '../types';

export {
  IssueStatus,
  type Issue,
  type Priority,
  normalizePriority,
} from '../types';

export {
  isCurrentStageApproval,
  classifyMergeDelivery,
  type MergeDeliveryStatus,
} from './issue-lifecycle';
