import type {
  CheckResultInput,
  FailureDetails,
  MaterializedTaskInput,
  StageRunState,
  TaskResultInput,
  WorkflowDefinition,
  WorkflowRun as WorkflowRunModel,
  WorkflowRunStatus as DomainWorkflowRunStatus,
  WorkflowStageId,
} from './model';
import type { WorkflowSourceDefinition } from './definition/workflow-definition-source';

export type Awaitable<T> = T | Promise<T>;

export type WorkflowRunId = string;

export type WorkflowRunStatus = DomainWorkflowRunStatus | 'completed';

export type WorkflowStageState = StageRunState;

export type WorkflowFailure = FailureDetails;

export type WorkflowDefinitionInput =
  | WorkflowDefinition
  | WorkflowSourceDefinition
  | { yaml: string };

export interface CreateWorkflowsInput {
  store: WorkflowStore;
  components?: WorkflowComponent[];
}

export interface Workflows {
  create(input: WorkflowCreateInput): Promise<WorkflowRunner>;
  load(id: WorkflowRunId): Promise<WorkflowRunner | null>;
  register(component: WorkflowComponent): void;
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

  run(): Promise<void>;
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

export type WorkflowComponent =
  | WorkflowTaskType
  | WorkflowCheckType
  | WorkflowTaskSourceType;

export interface WorkflowComponentContext {
  run: WorkflowExecutionContext;
}

export interface WorkflowExecutionContext {
  readonly id: WorkflowRunId;
  readonly status: WorkflowRunStatus;
  readonly currentStage: WorkflowStageId;
  readonly stages: WorkflowStageState[];
  readonly failure: WorkflowFailure | null;
}

export interface WorkflowTaskType {
  readonly type: 'task';
  readonly uses: string;
  run(input: WorkflowTaskInput): Awaitable<WorkflowTaskResult>;
}

export interface WorkflowCheckType {
  readonly type: 'check';
  readonly uses: string;
  run(input: WorkflowCheckInput): Awaitable<WorkflowCheckResult>;
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
  createTasks(input: WorkflowTaskSourceInput): Awaitable<WorkflowTaskSourceResult>;
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

export interface WorkflowTaskSourceInput {
  run: WorkflowExecutionContext;
  stage: WorkflowStageId;
  definition: {
    uses: string;
    with?: Record<string, unknown>;
  };
}

export type WorkflowTaskResult = TaskResultInput;

export type WorkflowCheckResult = CheckResultInput;

export interface WorkflowTaskSourceResult {
  tasks: MaterializedTaskInput[];
  state?: 'missing' | 'invalid' | 'empty';
}
