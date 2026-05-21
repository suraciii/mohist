import type {
  CheckResultInput,
  FailureDetails,
  MaterializedTaskInput,
  StageRunState,
  TaskResultInput,
  WorkflowDefinition,
  WorkflowRun as WorkflowRunModel,
  WorkflowRunStatus as DomainWorkflowRunStatus,
  ResolvedWorkflowDefinition,
  WorkflowStageId,
} from '../model';
import type { WorkflowSourceDefinition } from '../definition/workflow-definition-source';

export type Awaitable<T> = T | Promise<T>;

export type WorkflowRunId = string;

export type WorkflowRunStatus = DomainWorkflowRunStatus | 'completed';

export type WorkflowStageState = StageRunState;

export type WorkflowFailure = FailureDetails;

export type WorkflowStatus =
  | 'running'
  | 'completed'
  | 'awaiting-approval'
  | 'blocked'
  | 'failed'
  | 'stopped';

export type WorkflowDefinitionInput =
  | WorkflowDefinition
  | ResolvedWorkflowDefinition
  | WorkflowSourceDefinition
  | {
      yaml: string;
      source?: ResolvedWorkflowDefinition['source'];
      capturedAt?: string;
    };

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
  now?: string;
}

export interface WorkflowRunner {
  readonly id: WorkflowRunId;
  readonly status: WorkflowRunStatus;
  readonly currentStage: WorkflowStageId;
  readonly stages: WorkflowStageState[];
  readonly failure: WorkflowFailure | null;

  run(): Promise<WorkflowRunResult>;
  start(): Promise<WorkflowRunResult>;
  resume(): Promise<WorkflowRunResult>;
  pause(reason?: string): Promise<WorkflowRunResult>;

  approve(): Promise<WorkflowRunResult>;
  reject(reason?: string): Promise<WorkflowRunResult>;
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
  definition: WorkflowTasksFromDefinitionContext;
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
}

export interface WorkflowTaskDefinitionContext {
  id: string;
  title: string;
  uses?: string;
  with?: Record<string, unknown>;
}

export interface WorkflowCheckDefinitionContext {
  name: string;
  title: string;
  uses?: string;
  with?: Record<string, unknown>;
}

export interface WorkflowTasksFromDefinitionContext {
  uses: string;
  with?: Record<string, unknown>;
}
