import type {
  CheckResultInput,
  MaterializedTaskInput,
  StageRunSnapshot,
  TaskResultInput,
  FailureDetails,
  WorkflowDefinition,
  WorkflowDefinitionSnapshot,
  WorkflowStageId,
} from './model';
import type { WorkflowSourceDefinition } from './definition/workflow-definition-source';

export type Awaitable<T> = T | Promise<T>;

export type WorkflowRunId = string;

export type WorkflowStateStatus = 'running' | 'completed' | 'failed' | 'cancelled';

export type WorkflowStageState = StageRunSnapshot;

export type WorkflowFailure = FailureDetails;

export interface WorkflowState {
  id: WorkflowRunId;
  status: WorkflowStateStatus;
  currentStage: WorkflowStageId;
  stageOrder: WorkflowStageId[];
  definition: WorkflowDefinitionSnapshot;
  stages: WorkflowStageState[];
  failure: WorkflowFailure | null;
}

export type WorkflowStatus =
  | 'running'
  | 'completed'
  | 'awaiting-approval'
  | 'blocked'
  | 'failed'
  | 'stopped';

export type WorkflowDefinitionInput =
  | WorkflowDefinition
  | WorkflowDefinitionSnapshot
  | WorkflowSourceDefinition
  | {
      yaml: string;
      source?: WorkflowDefinitionSnapshot['source'];
      capturedAt?: string;
    };

export interface CreateWorkflowsInput {
  store: WorkflowStore;
  components?: WorkflowComponent[];
}

export interface Workflows {
  create(input: WorkflowCreateInput): Promise<Workflow>;
  load(id: WorkflowRunId): Promise<Workflow | null>;
  register(component: WorkflowComponent): void;
}

export interface WorkflowCreateInput {
  id: WorkflowRunId;
  definition: WorkflowDefinitionInput;
  now?: string;
}

export interface Workflow {
  readonly id: WorkflowRunId;
  readonly state: WorkflowState;

  start(): Promise<WorkflowRunResult>;
  resume(): Promise<WorkflowRunResult>;
  pause(reason?: string): Promise<WorkflowRunResult>;

  approve(): Promise<WorkflowRunResult>;
  reject(reason?: string): Promise<WorkflowRunResult>;
}

export interface WorkflowStore {
  load(id: WorkflowRunId): Awaitable<WorkflowState | null>;
  save(state: WorkflowState): Awaitable<void>;
}

export type WorkflowComponent =
  | WorkflowTaskType
  | WorkflowCheckType
  | WorkflowTaskSourceType;

export interface WorkflowComponentContext {
  state: WorkflowState;
}

export interface WorkflowTaskType {
  readonly type: 'task';
  readonly uses: string;
  create(context: WorkflowComponentContext): WorkflowTask;
}

export interface WorkflowCheckType {
  readonly type: 'check';
  readonly uses: string;
  create(context: WorkflowComponentContext): WorkflowCheck;
}

export interface WorkflowTaskSourceType {
  readonly type: 'task-source';
  readonly uses: string;
  create(context: WorkflowComponentContext): WorkflowTaskSource;
}

export interface WorkflowTask {
  run(input: WorkflowTaskInput): Awaitable<WorkflowTaskResult>;
}

export interface WorkflowCheck {
  run(input: WorkflowCheckInput): Awaitable<WorkflowCheckResult>;
}

export interface WorkflowTaskSource {
  run(input: WorkflowTaskSourceInput): Awaitable<WorkflowTaskSourceResult>;
}

export interface WorkflowTaskInput {
  state: WorkflowState;
  stage: WorkflowStageId;
  taskId: string;
}

export interface WorkflowCheckInput {
  state: WorkflowState;
  stage: WorkflowStageId;
  checkName: string;
}

export interface WorkflowTaskSourceInput {
  state: WorkflowState;
  stage: WorkflowStageId;
}

export type WorkflowTaskResult = TaskResultInput;

export type WorkflowCheckResult = CheckResultInput;

export interface WorkflowTaskSourceResult {
  tasks: MaterializedTaskInput[];
  state?: 'missing' | 'invalid' | 'empty';
}

export interface WorkflowRunResult {
  status: WorkflowStatus;
  stage: WorkflowStageId;
  message?: string;
  state: WorkflowState;
}
