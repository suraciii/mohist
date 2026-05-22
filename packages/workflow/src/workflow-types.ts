import type {
  FailureDetails,
  MaterializedTaskInput,
  StageRunState,
  WorkflowRun as WorkflowRunModel,
  WorkflowRunStatus as DomainWorkflowRunStatus,
  WorkflowStageId,
} from './model';
import type { Registry } from './registry';
import type { WorkflowSourceDefinition } from './definition/workflow-definition-source';
import type { WorkflowDefinition } from './model/workflow-definition';

export type Awaitable<T> = T | Promise<T>;
export type WorkflowRunId = string;
export type WorkflowRunStatus = DomainWorkflowRunStatus | 'completed';
export type WorkflowStageState = StageRunState;
export type WorkflowFailure = FailureDetails;

export type WorkflowDefinitionInput =
  | WorkflowDefinition
  | WorkflowSourceDefinition
  | { yaml: string };

export interface CreateWorkflowRuntimeInput {
  store: WorkflowStore;
  registry: Registry;
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

export interface WorkflowTaskInput {
  id: string;
  title: string;
  with?: Record<string, unknown>;
}

export interface WorkflowCheckInput {
  name: string;
  title: string;
  with?: Record<string, unknown>;
}

export interface WorkflowTaskSourceResult {
  tasks: MaterializedTaskInput[];
  state?: 'missing' | 'invalid' | 'empty';
}
