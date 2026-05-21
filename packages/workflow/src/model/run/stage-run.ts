import type { CheckFailurePolicy, CheckPhase, CheckPolicy, CompiledStageDefinition, WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { CheckState } from './check-state';
import { TaskRun } from './task-run';
import { baseRuntimeTaskId, escapeRegExp, type ApprovalState, type CausedByMetadata, type CommitPoint, type FailureDetails, type MaterializedTaskInput, type StageRunState, type StageRunStatus, type WorkSourceState } from './types';

interface TaskEntry {
  id: string;
  title: string;
  uses?: string;
  run: TaskRun;
  events: string[];
  output: unknown | null;
  reason: string | null;
}

export class StageRun {
  readonly tasks: TaskEntry[];
  readonly checks: CheckState[];
  status: StageRunStatus = 'pending';
  attemptSequence = 1;
  approval: ApprovalState | null = null;
  failure: FailureDetails | null = null;
  commitPoint: CommitPoint | null = null;
  workSourceState: WorkSourceState = { evaluated: false };

  constructor(
    readonly definition: CompiledStageDefinition,
    readonly order: number,
  ) {
    this.tasks = definition.tasks.map(task => this.createTaskEntry(task.id, task.title, task.uses));
    this.checks = definition.checks.map(check => new CheckState(check.name, check.title));
  }

  get stage(): WorkflowStageId {
    return this.definition.stage;
  }

  start(): void {
    if (this.status !== 'pending') {
      throw new WorkflowDomainError(`Stage ${this.stage} cannot start from ${this.status}`);
    }
    this.status = 'running';
  }

  materializeTasks(tasks: MaterializedTaskInput[]): void {
    for (const task of [...tasks].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))) {
      const existing = this.tasks.find(candidate => candidate.id === task.id);
      if (existing) {
        continue;
      }
      const taskRun = this.createTaskEntry(task.id, task.title, task.uses);
      this.tasks.push(taskRun);
    }
  }

  nextTask(): TaskEntry | null {
    return this.currentTasks().find(task => !this.isTaskTerminal(task)) ?? null;
  }

  nextCheck(phase?: 'pre-task' | 'post-task'): CheckState | null {
    if (phase === 'post-task' && !this.allRequiredTasksTerminal()) return null;
    if (phase === undefined && !this.allRequiredTasksTerminal()) return null;
    for (const policy of this.nonApprovalCheckPolicies()) {
      if (phase && policy.phase !== phase) continue;
      const check = this.checks.find(candidate => candidate.name === policy.checkName);
      if (check && check.status !== 'passed') return check;
    }
    return null;
  }

  checkPhase(checkName: string): CheckPhase {
    return this.nonApprovalCheckPolicies().find(policy => policy.checkName === checkName)?.phase ?? 'post-task';
  }

  nonApprovalCheckPolicies(): CheckPolicy[] {
    if (this.definition.checkPolicies) {
      return this.definition.checkPolicies.filter(policy => policy.phase !== 'approval');
    }
    return this.definition.checks.map(check => ({ checkName: check.name, phase: 'post-task' as const }));
  }

  requiresApproval(): boolean {
    if (this.definition.requiresApproval === false) return false;
    return Boolean(this.definition.approvalPolicy ?? this.definition.requiresApproval);
  }

  allRequiredTasksTerminal(): boolean {
    return this.currentTasks().every(task => this.isTaskTerminal(task));
  }

  allRequiredTasksSucceeded(): boolean {
    return this.currentTasks().every(task => task.run.status === 'completed');
  }

  hasFailedTask(): boolean {
    return this.currentTasks().some(task => task.run.status === 'failed');
  }

  currentTasks(): TaskEntry[] {
    const latestByBaseTaskId = new Map<string, TaskEntry>();
    for (const task of this.tasks) {
      const baseTaskId = baseRuntimeTaskId(task.id);
      const existing = latestByBaseTaskId.get(baseTaskId);
      if (!existing || this.tasks.indexOf(task) > this.tasks.indexOf(existing)) {
        latestByBaseTaskId.set(baseTaskId, task);
      }
    }
    return this.tasks.filter(task => latestByBaseTaskId.get(baseRuntimeTaskId(task.id)) === task);
  }

  appendTaskRun(taskId: string): TaskEntry {
    const baseTaskId = baseRuntimeTaskId(taskId);
    const latest = this.latestTaskRun(baseTaskId);
    if (!latest) throw new WorkflowDomainError(`Task ${taskId} does not exist in stage ${this.stage}`);
    const nextIndex = this.nextTaskRunIndex(baseTaskId);
    const id = `${baseTaskId}:${nextIndex}`;
    const task = this.createTaskEntry(id, latest.title, latest.uses);
    this.tasks.push(task);
    return task;
  }

  private latestTaskRun(baseTaskId: string): TaskEntry | undefined {
    return this.tasks
      .filter(task => baseRuntimeTaskId(task.id) === baseTaskId)
      .at(-1);
  }

  private nextTaskRunIndex(baseTaskId: string): number {
    let max = this.tasks.some(task => task.id === baseTaskId) ? 0 : -1;
    for (const task of this.tasks) {
      const match = task.id.match(new RegExp(`^${escapeRegExp(baseTaskId)}:(\\d+)$`));
      if (match) max = Math.max(max, Number(match[1]));
    }
    return max + 1;
  }

  allChecksPassed(): boolean {
    const nonApprovalCheckNames = new Set(this.nonApprovalCheckPolicies().map(policy => policy.checkName));
    return this.checks
      .filter(check => nonApprovalCheckNames.has(check.name))
      .every(check => check.status === 'passed');
  }

  findTask(taskId: string): TaskEntry {
    const task = taskId === baseRuntimeTaskId(taskId)
      ? this.currentTasks().find(candidate => baseRuntimeTaskId(candidate.id) === taskId)
      : this.tasks.find(candidate => candidate.id === taskId);
    if (!task) throw new WorkflowDomainError(`Task ${taskId} does not exist in stage ${this.stage}`);
    return task;
  }

  findCheck(checkName: string): CheckState {
    const check = this.checks.find(candidate => candidate.name === checkName);
    if (!check) throw new WorkflowDomainError(`Check ${checkName} does not exist in stage ${this.stage}`);
    return check;
  }

  resetTask(taskId: string): void {
    const task = this.findTask(taskId);
    task.run.resetForFreshAttempt();
    task.events = [];
    task.output = null;
    task.reason = null;
  }

  resetCheck(checkName: string): void {
    const check = this.findCheck(checkName);
    check.resetForFreshAttempt();
  }

  resetTaskAndDownstream(taskId: string): void {
    const task = this.findTask(taskId);
    const boundaryIndex = this.tasks.indexOf(task);

    for (const t of this.tasks.slice(boundaryIndex)) {
      t.run.resetForFreshAttempt();
      t.events = [];
      t.output = null;
      t.reason = null;
    }
  }

  resetCheckAndDownstream(checkName: string): void {
    const boundaryIndex = this.checks.findIndex(c => c.name === checkName);

    for (const [index, c] of this.checks.entries()) {
      if (index >= boundaryIndex) {
        c.resetForFreshAttempt();
      }
    }
  }

  scheduledRetryTaskCount(checkName: string): number {
    const policy = this.definition.checkFailurePolicies?.find(candidate => candidate.checkName === checkName);
    if (!policy) return 0;
    return this.tasks.filter(task => task.id === policy.retryTaskId || task.id.startsWith(`${policy.retryTaskId}:`)).length;
  }

  appendRetryTask(policy: CheckFailurePolicy, causedBy: CausedByMetadata): TaskEntry {
    const suffix = this.scheduledRetryTaskCount(policy.checkName);
    const id = this.tasks.some(task => task.id === policy.retryTaskId) ? `${policy.retryTaskId}:${suffix}` : policy.retryTaskId;
    const taskDefinition = this.retryTaskDefinition(policy.retryTaskId);
    const task = this.createTaskEntry(id, policy.retryTaskTitle, taskDefinition?.uses);
    task.reason = causedBy.message ?? `Retry after ${policy.checkName}`;
    this.tasks.push(task);
    return task;
  }

  private retryTaskDefinition(taskId: string) {
    const baseTaskId = baseRuntimeTaskId(taskId);
    return this.definition.checks
      .map(check => check.onFailure?.retry?.task)
      .find((task): task is NonNullable<typeof task> => Boolean(task && (task.id === taskId || task.id === baseTaskId)));
  }

  reopenForRecovery(): void {
    this.status = 'running';
    this.failure = null;
    this.approval = null;
  }

  appendAdHocTask(id: string, title: string, causedBy: CausedByMetadata, uses?: string): TaskEntry {
    const task = this.createTaskEntry(id, title, uses);
    task.reason = causedBy.message ?? title;
    this.tasks.push(task);
    return task;
  }

  removeGeneratedTasks(): void {
    const retryTaskIds = new Set([
      ...(this.definition.checkFailurePolicies?.map(policy => policy.retryTaskId) ?? []),
    ]);
    const staticTaskIds = new Set(this.definition.tasks.map(task => task.id));
    for (let index = this.tasks.length - 1; index >= 0; index--) {
      const task = this.tasks[index];
      const isRetryTask = [...retryTaskIds].some(retryTaskId => task.id === retryTaskId || task.id.startsWith(`${retryTaskId}:`));
      const isRuntimeTask = !staticTaskIds.has(task.id);
      if (isRetryTask || isRuntimeTask) {
        this.tasks.splice(index, 1);
      }
    }
  }

  removeNonStaticTasks(): void {
    const staticTaskIds = new Set(this.definition.tasks.map(task => task.id));
    for (let index = this.tasks.length - 1; index >= 0; index--) {
      if (!staticTaskIds.has(this.tasks[index].id)) {
        this.tasks.splice(index, 1);
      }
    }
  }

  recordWorkSourceEvaluated(tasks: MaterializedTaskInput[]): void {
    this.workSourceState = { evaluated: true, tasks };
  }

  recordWorkSourceMissing(): void {
    this.workSourceState = { evaluated: true, missing: true };
  }

  recordWorkSourceInvalid(): void {
    this.workSourceState = { evaluated: true, invalid: true };
  }

  recordWorkSourceEmpty(): void {
    this.workSourceState = { evaluated: true, empty: true };
  }

  resetWorkSourceState(): void {
    this.workSourceState = { evaluated: false };
  }

  restoreTaskState(id: string, title: string, uses?: string): TaskEntry {
    const existing = this.tasks.find(task => task.id === id);
    if (existing) return existing;
    const task = this.createTaskEntry(id, title, uses);
    this.tasks.push(task);
    return task;
  }

  restoreCheckState(name: string, title: string): CheckState {
    const existing = this.checks.find(check => check.name === name);
    if (existing) return existing;
    const check = new CheckState(name, title);
    this.checks.push(check);
    return check;
  }

  requestApproval(now: string, output: unknown = null): void {
    this.status = 'awaiting-approval';
    this.approval = {
      status: 'awaiting',
      output,
      requestedAt: now,
      respondedAt: null,
    };
  }

  state(): StageRunState {
    return {
      stage: this.stage,
      status: this.status,
      order: this.order,
      attemptSequence: this.attemptSequence,
      tasks: this.tasks.map(task => ({
        id: task.id,
        title: task.title,
        uses: task.uses,
        status: task.run.status,
        events: [...task.events],
        output: task.output,
        reason: task.reason,
      })),
      checks: this.checks.map(check => check.state()),
      approval: this.approval ? { ...this.approval } : null,
      failure: this.failure,
      commitPoint: this.commitPoint,
      workSourceState: this.hasDynamicWorkSource() ? this.workSourceState : undefined,
    };
  }

  hasDynamicWorkSource(): boolean {
    return Boolean(this.definition.tasksFrom);
  }

  private isTaskTerminal(task: TaskEntry): boolean {
    return task.run.status === 'completed' || task.run.status === 'failed';
  }

  private createTaskEntry(id: string, title: string, uses?: string): TaskEntry {
    return {
      id,
      title,
      uses,
      run: new TaskRun(),
      events: [],
      output: null,
      reason: null,
    };
  }
}
