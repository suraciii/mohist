import type {
  FailureDetails,
  StageRunState,
  WorkflowRunStatus as DomainWorkflowRunStatus,
} from '../domain/run/types';
import type { WorkflowRun as WorkflowRunModel } from '../domain/run/workflow-run';
import type { WorkflowStageId } from '../domain/workflow-definition';
import type { TaskHandler, CheckHandler, TaskLoader } from '../handlers';
import type { WorkflowDefinitionInput } from '../definition';

export type Awaitable<T> = T | Promise<T>;
export type WorkflowRunId = string;
export type WorkflowRunStatus = DomainWorkflowRunStatus | 'completed';
export type WorkflowStageState = StageRunState;
export type WorkflowFailure = FailureDetails;

export interface CreateWorkflowRuntimeInput {
  store: WorkflowStore;
  tasks?: Record<string, TaskHandler>;
  checks?: Record<string, CheckHandler>;
  taskLoaders?: Record<string, TaskLoader>;
}

export interface WorkflowRuntime {
  create(input: WorkflowCreateInput): Promise<WorkflowRunner>;
  load(id: WorkflowRunId): Promise<WorkflowRunner | null>;
}

export interface WorkflowCreateInput {
  id: WorkflowRunId;
  definition: WorkflowDefinitionInput;
}

export interface WorkflowRunner {
  readonly id: WorkflowRunId;
  readonly status: WorkflowRunStatus;
  readonly currentStage: WorkflowStageId;
  readonly stages: WorkflowStageState[];
  readonly failure: WorkflowFailure | null;

  start(): Promise<void>;
  resume(): Promise<void>;
  pause(reason?: string): Promise<void>;

  approve(): Promise<void>;
  reject(reason?: string): Promise<void>;
}

export interface WorkflowStore {
  load(id: WorkflowRunId): Awaitable<WorkflowRunModel | null>;
  save(run: WorkflowRunModel): Awaitable<void>;
}
