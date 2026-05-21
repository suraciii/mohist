import type {
  CheckResultInput,
  CheckStateSnapshot,
  MaterializedTaskInput,
  StageRunSnapshot,
  TaskResultInput,
  FailureDetails,
  TaskRunSnapshot,
  WorkflowDefinition,
  WorkflowDefinitionSnapshot,
  WorkflowRunStatus as DomainWorkflowRunStatus,
  WorkflowStageId,
} from './model';
import {
  createWorkflowDefinitionSnapshot,
  WorkflowRun,
} from './model';
import { parseWorkflowDefinitionSource, type WorkflowSourceDefinition } from './definition/workflow-definition-source';
import YAML from 'yaml';

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
  maxSteps?: number;
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
  definition: WorkflowTaskDefinitionContext;
}

export interface WorkflowCheckInput {
  state: WorkflowState;
  stage: WorkflowStageId;
  checkName: string;
  definition: WorkflowCheckDefinitionContext;
}

export interface WorkflowTaskSourceInput {
  state: WorkflowState;
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
  state: WorkflowState;
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

export function createWorkflows(input: CreateWorkflowsInput): Workflows {
  const registry = new WorkflowComponentRegistry();
  for (const component of input.components ?? []) {
    registry.register(component);
  }

  return {
    async create(createInput) {
      const definition = workflowDefinitionSnapshotFromInput(createInput.definition, createInput.now);
      const { run } = WorkflowRun.startWorkflow({
        id: createInput.id,
        issueId: createInput.id,
        issueNumber: 0,
        workflowDefinitionSnapshot: definition,
        now: createInput.now,
      });
      const workflow = new RunnableWorkflow(run, input.store, registry, input.maxSteps);
      return workflow;
    },

    async load(id) {
      const state = await input.store.load(id);
      if (!state) return null;
      return new RunnableWorkflow(workflowRunFromState(state), input.store, registry, input.maxSteps);
    },

    register(component) {
      registry.register(component);
    },
  };
}

class WorkflowComponentRegistry {
  private readonly tasks = new Map<string, WorkflowTaskType>();
  private readonly checks = new Map<string, WorkflowCheckType>();
  private readonly taskSources = new Map<string, WorkflowTaskSourceType>();

  register(component: WorkflowComponent): void {
    if (component.type === 'task') {
      this.tasks.set(component.uses, component);
      return;
    }
    if (component.type === 'check') {
      this.checks.set(component.uses, component);
      return;
    }
    this.taskSources.set(component.uses, component);
  }

  task(uses: string | undefined): WorkflowTaskType | null {
    if (!uses) return null;
    return this.tasks.get(uses) ?? null;
  }

  check(uses: string | undefined): WorkflowCheckType | null {
    if (!uses) return null;
    return this.checks.get(uses) ?? null;
  }

  taskSource(uses: string | undefined): WorkflowTaskSourceType | null {
    if (!uses) return null;
    return this.taskSources.get(uses) ?? null;
  }
}

type NextExecution =
  | { type: 'task'; stage: WorkflowStageId; taskId: string }
  | { type: 'check'; stage: WorkflowStageId; checkName: string }
  | { type: 'task-source'; stage: WorkflowStageId }
  | { type: 'terminal'; result: Omit<WorkflowRunResult, 'state'> };

class RunnableWorkflow implements Workflow {
  constructor(
    private readonly run: WorkflowRun,
    private readonly store: WorkflowStore,
    private readonly registry: WorkflowComponentRegistry,
    private readonly maxSteps = 1000,
  ) {}

  get id(): WorkflowRunId {
    return this.run.id;
  }

  get state(): WorkflowState {
    return stateFromRun(this.run);
  }

  async start(): Promise<WorkflowRunResult> {
    await this.persist();
    return this.resume();
  }

  async resume(): Promise<WorkflowRunResult> {
    let steps = 0;
    while (steps++ < this.maxSteps) {
      const next = selectNextExecution(this.run);
      if (next.type === 'terminal') return this.result(next.result);

      if (next.type === 'task-source') {
        const result = await this.runTaskSource(next.stage);
        this.run.materializeTasks(next.stage, result.tasks, result.state);
        await this.persist();
        continue;
      }

      if (next.type === 'task') {
        const result = await this.runTask(next.stage, next.taskId);
        this.run.completeTask(next.stage, next.taskId, result);
        await this.persist();
        continue;
      }

      const result = await this.runCheck(next.stage, next.checkName);
      this.run.recordCheckResult(next.stage, {
        ...result,
        name: result.name ?? next.checkName,
      });
      await this.persist();
    }

    return this.result({
      status: 'stopped',
      stage: this.run.currentStage,
      message: `Workflow stopped after ${this.maxSteps} steps`,
    });
  }

  async pause(reason?: string): Promise<WorkflowRunResult> {
    this.run.interruptRunningWorkAttempts(reason ?? 'workflow-paused');
    await this.persist();
    return this.result({
      status: 'stopped',
      stage: this.run.currentStage,
      message: reason ?? 'Workflow paused',
    });
  }

  async approve(): Promise<WorkflowRunResult> {
    this.run.approveStage(this.run.currentStage);
    await this.persist();
    return this.resume();
  }

  async reject(reason?: string): Promise<WorkflowRunResult> {
    this.run.rejectStage(this.run.currentStage, { output: reason });
    await this.persist();
    return this.result(statusResultFromRun(this.run));
  }

  async persist(): Promise<void> {
    await this.store.save(this.state);
  }

  private async runTaskSource(stage: WorkflowStageId): Promise<WorkflowTaskSourceResult> {
    const sourceDefinition = taskSourceDefinition(this.run.workflowDefinitionSnapshot, stage);
    const component = this.registry.taskSource(sourceDefinition?.uses);
    if (!component || !sourceDefinition) {
      return { tasks: [], state: 'missing' };
    }
    try {
      return await component.create({ state: this.state }).run({
        state: this.state,
        stage,
        definition: sourceDefinition,
      });
    } catch {
      return { tasks: [], state: 'invalid' };
    }
  }

  private async runTask(stage: WorkflowStageId, taskId: string): Promise<WorkflowTaskResult> {
    const definition = taskDefinition(this.run.workflowDefinitionSnapshot, stage, taskId);
    const component = this.registry.task(definition?.uses);
    if (!component || !definition) {
      return {
        status: 'failed',
        reason: `No task registered for ${definition?.uses ?? taskId}`,
      };
    }
    try {
      return await component.create({ state: this.state }).run({
        state: this.state,
        stage,
        taskId,
        definition,
      });
    } catch (error) {
      return {
        status: 'failed',
        reason: errorMessage(error),
      };
    }
  }

  private async runCheck(stage: WorkflowStageId, checkName: string): Promise<WorkflowCheckResult> {
    const definition = checkDefinition(this.run.workflowDefinitionSnapshot, stage, checkName);
    const component = this.registry.check(definition?.uses);
    if (!component || !definition) {
      return {
        name: checkName,
        status: 'error',
        message: `No check registered for ${definition?.uses ?? checkName}`,
      };
    }
    try {
      return await component.create({ state: this.state }).run({
        state: this.state,
        stage,
        checkName,
        definition,
      });
    } catch (error) {
      return {
        name: checkName,
        status: 'error',
        message: errorMessage(error),
      };
    }
  }

  private result(result: Omit<WorkflowRunResult, 'state'>): WorkflowRunResult {
    return { ...result, state: this.state };
  }
}

function selectNextExecution(run: WorkflowRun): NextExecution {
  const work = run.nextWork();
  if (work.kind === 'complete') {
    return { type: 'terminal', result: { status: 'completed', stage: run.currentStage, message: 'Workflow completed' } };
  }
  if (work.kind === 'failed') {
    return {
      type: 'terminal',
      result: {
        status: 'failed',
        stage: work.reason.stage,
        message: work.reason.message ?? work.reason.reason,
      },
    };
  }
  if (work.kind === 'await-approval') {
    return {
      type: 'terminal',
      result: { status: 'awaiting-approval', stage: work.stage, message: `Awaiting ${work.stage} approval` },
    };
  }
  if (work.kind === 'blocked') {
    if (!work.reason.complete && work.reason.reason === 'dynamic-source-not-evaluated') {
      return { type: 'task-source', stage: work.stage };
    }
    return {
      type: 'terminal',
      result: { status: 'blocked', stage: work.stage, message: blockedReasonMessage(work.reason) },
    };
  }
  if (work.kind === 'task') return { type: 'task', stage: work.stage, taskId: work.taskId };
  return { type: 'check', stage: work.stage, checkName: work.checkName };
}

function statusResultFromRun(run: WorkflowRun): Omit<WorkflowRunResult, 'state'> {
  const work = run.nextWork();
  const next = selectNextExecution(run);
  return next.type === 'terminal'
    ? next.result
    : { status: 'running', stage: 'stage' in work ? work.stage : run.currentStage };
}

function blockedReasonMessage(reason: Extract<ReturnType<WorkflowRun['nextWork']>, { kind: 'blocked' }>['reason']): string {
  if (reason.complete) return 'Workflow is blocked';
  if ('taskId' in reason) return `${reason.reason}: ${reason.taskId}`;
  if ('checkName' in reason) return `${reason.reason}: ${reason.checkName}`;
  if ('stage' in reason) return `${reason.reason}: ${reason.stage}`;
  return 'Workflow is blocked';
}

function stateFromRun(run: WorkflowRun): WorkflowState {
  const snapshot = run.snapshot();
  return {
    id: snapshot.id,
    status: workflowStateStatusFromDomain(snapshot.status),
    currentStage: snapshot.currentStage,
    stageOrder: [...snapshot.stageOrder],
    definition: snapshot.workflowDefinitionSnapshot,
    stages: snapshot.stageRuns,
    failure: snapshot.failure,
  };
}

function workflowStateStatusFromDomain(status: DomainWorkflowRunStatus): WorkflowStateStatus {
  if (status === 'passed') return 'completed';
  return status;
}

function domainStatusFromWorkflowState(status: WorkflowStateStatus): DomainWorkflowRunStatus {
  if (status === 'completed') return 'passed';
  return status;
}

function workflowRunFromState(state: WorkflowState): WorkflowRun {
  const snapshot = {
    id: state.id,
    issueId: state.id,
    issueNumber: 0,
    status: domainStatusFromWorkflowState(state.status),
    currentStage: state.currentStage,
    stageOrder: [...state.stageOrder],
    workflowDefinitionSnapshot: state.definition,
    stageRuns: state.stages,
    failure: state.failure,
  };
  const { run } = WorkflowRun.startWorkflow({
    id: snapshot.id,
    issueId: snapshot.issueId,
    issueNumber: snapshot.issueNumber,
    workflowDefinitionSnapshot: snapshot.workflowDefinitionSnapshot,
  });
  restoreRunFields(run, snapshot);
  return run;
}

function restoreRunFields(run: WorkflowRun, snapshot: ReturnType<WorkflowRun['snapshot']>): void {
  run.status = snapshot.status;
  run.currentStage = snapshot.currentStage;
  run.failure = snapshot.failure;

  for (const stageSnapshot of snapshot.stageRuns) {
    const stageRun = run.stageRun(stageSnapshot.stage);
    stageRun.status = stageSnapshot.status;
    stageRun.attemptSequence = stageSnapshot.attemptSequence ?? stageRun.attemptSequence;
    stageRun.approval = stageSnapshot.approval ? { ...stageSnapshot.approval } : null;
    stageRun.failure = stageSnapshot.failure;
    stageRun.commitPoint = stageSnapshot.commitPoint;
    stageRun.workSourceState = stageSnapshot.workSourceState ?? { evaluated: false };
    restoreTasks(stageRun, stageSnapshot.tasks);
    restoreChecks(stageRun, stageSnapshot.checks);
  }
}

function restoreTasks(stageRun: ReturnType<WorkflowRun['currentStageRun']>, tasks: TaskRunSnapshot[]): void {
  stageRun.tasks.splice(0, stageRun.tasks.length);
  for (const taskSnapshot of tasks) {
    const task = stageRun.materializeTaskForPersistence(
      taskSnapshot.id,
      taskSnapshot.title,
      taskSnapshot.order,
      taskSnapshot.uses,
    );
    task.status = taskSnapshot.status;
    task.dependsOn = [...taskSnapshot.dependsOn];
    task.attempts = taskSnapshot.attempts;
    task.duration = taskSnapshot.duration;
    task.artifacts = [...taskSnapshot.artifacts];
    task.events = [...taskSnapshot.events];
    task.output = taskSnapshot.output;
    task.reason = taskSnapshot.reason;
    task.causedBy = taskSnapshot.causedBy;
    task.resetBy = taskSnapshot.resetBy;
    task.latestAttempt = taskSnapshot.latestAttempt;
  }
}

function restoreChecks(stageRun: ReturnType<WorkflowRun['currentStageRun']>, checks: CheckStateSnapshot[]): void {
  stageRun.checks.splice(0, stageRun.checks.length);
  for (const checkSnapshot of checks) {
    const check = stageRun.materializeCheckForPersistence(checkSnapshot.name, checkSnapshot.title);
    check.status = checkSnapshot.status;
    check.message = checkSnapshot.message;
    check.output = checkSnapshot.output;
    check.runCount = checkSnapshot.runCount;
    check.latestAttempt = checkSnapshot.latestAttempt;
  }
}

function workflowDefinitionSnapshotFromInput(input: WorkflowDefinitionInput, capturedAt?: string): WorkflowDefinitionSnapshot {
  if (isWorkflowDefinitionSnapshot(input)) return input;
  if (isYamlWorkflowInput(input)) {
    const parsed = YAML.parse(input.yaml);
    return createWorkflowDefinitionSnapshot({
      definition: parseWorkflowDefinitionSource(normalizeWorkflowSource(parsed)),
      source: input.source,
      capturedAt: input.capturedAt ?? capturedAt,
    });
  }
  if (isWorkflowSourceDefinition(input)) {
    return createWorkflowDefinitionSnapshot({
      definition: parseWorkflowDefinitionSource(input),
      capturedAt,
    });
  }
  return createWorkflowDefinitionSnapshot({ definition: input, capturedAt });
}

function taskSourceDefinition(snapshot: WorkflowDefinitionSnapshot, stageId: WorkflowStageId): WorkflowTasksFromDefinitionContext | null {
  const source = snapshot.compiledStageDefinitions.find(stage => stage.stage === stageId)?.tasksFrom;
  if (!source) return null;
  if (typeof source === 'string') return { uses: source };
  return {
    uses: source.uses,
    with: source.with ? { ...source.with } : undefined,
  };
}

function taskDefinition(snapshot: WorkflowDefinitionSnapshot, stageId: WorkflowStageId, taskId: string): WorkflowTaskDefinitionContext | null {
  const baseTaskId = baseRuntimeTaskId(taskId);
  const stage = snapshot.compiledStageDefinitions.find(candidate => candidate.stage === stageId);
  const task = stage?.tasks.find(candidate => candidate.id === taskId || candidate.id === baseTaskId)
    ?? stage?.checks
      .map(check => check.onFailure?.retry?.task)
      .find(candidate => candidate && (candidate.id === taskId || candidate.id === baseTaskId));
  if (!task) return null;
  return {
    id: taskId,
    title: task.title,
    uses: task.uses,
    with: task.with ? { ...task.with } : undefined,
  };
}

function checkDefinition(snapshot: WorkflowDefinitionSnapshot, stageId: WorkflowStageId, checkName: string): WorkflowCheckDefinitionContext | null {
  const check = snapshot.compiledStageDefinitions
    .find(candidate => candidate.stage === stageId)
    ?.checks.find(candidate => candidate.name === checkName);
  if (!check) return null;
  return {
    name: check.name,
    title: check.title,
    uses: check.uses,
    with: check.with ? { ...check.with } : undefined,
  };
}

function baseRuntimeTaskId(taskId: string): string {
  return taskId.replace(/:\d+$/, '');
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isWorkflowDefinitionSnapshot(value: unknown): value is WorkflowDefinitionSnapshot {
  return isPlainObject(value)
    && 'workflowId' in value
    && 'resolvedDefinition' in value
    && 'compiledStageDefinitions' in value;
}

function isYamlWorkflowInput(value: WorkflowDefinitionInput): value is Extract<WorkflowDefinitionInput, { yaml: string }> {
  return Boolean(value && typeof value === 'object' && 'yaml' in value && typeof value.yaml === 'string');
}

function normalizeWorkflowSource(value: unknown): WorkflowSourceDefinition {
  const source = isPlainObject(value) && isPlainObject(value.workflow)
    ? value.workflow
    : value;
  if (!isWorkflowSourceDefinition(source)) {
    throw new Error('Workflow YAML must define workflow id and stages');
  }
  return source;
}

function isWorkflowSourceDefinition(value: unknown): value is WorkflowSourceDefinition {
  return Boolean(
    isPlainObject(value)
      && typeof value.id === 'string'
      && Array.isArray(value.stages),
  );
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value));
}
