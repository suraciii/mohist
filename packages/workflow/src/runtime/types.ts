import type {
  FailureDetails,
  StageRunState,
  WorkflowRunStatus as DomainWorkflowRunStatus,
} from '../domain/run/types';
import type { WorkflowRun as WorkflowRunModel } from '../domain/run/workflow-run';
import type { WorkflowStageId } from '../domain/workflow-definition';
import type { TaskHandler, CheckHandler, TaskLoader } from '../handlers';
import type { WorkflowInput } from '../parser';

export type Awaitable<T> = T | Promise<T>;
export type WorkflowRunId = string;
export type WorkflowRunStatus = DomainWorkflowRunStatus | 'completed';
export type WorkflowStageState = StageRunState;
export type WorkflowFailure = FailureDetails;

export interface WorkflowRuntimeOptions {
  store: WorkflowStore;
  tasks?: Record<string, TaskHandler>;
  checks?: Record<string, CheckHandler>;
  taskLoaders?: Record<string, TaskLoader>;
}

export interface WorkflowCreateInput {
  id: WorkflowRunId;
  definition: WorkflowInput;
}

export interface WorkflowRunner {
  readonly id: WorkflowRunId;
  readonly status: WorkflowRunStatus;
  readonly currentStage: WorkflowStageId;
  readonly stages: WorkflowStageState[];
  readonly failure: WorkflowFailure | null;

  withSignal(signal: AbortSignal): this;
  start(): Promise<void>;
  run(): Promise<void>;
  resume(): Promise<void>;
  pause(reason?: string): Promise<void>;

  approve(): Promise<void>;
  reject(reason?: string): Promise<void>;
  retry(): Promise<void>;
  rerun(): Promise<void>;
}

export interface WorkflowStore {
  load(id: WorkflowRunId): Awaitable<WorkflowRunModel | null>;
  save(run: WorkflowRunModel): Awaitable<void>;
}
