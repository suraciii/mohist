import type { CheckFailurePolicy, CheckPhase, CheckPolicy, CompiledStageDefinition, WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { CheckState } from './check-state';
import { TaskRun } from './task-run';
import { baseRuntimeTaskId, escapeRegExp, type ApprovalState, type CausedByMetadata, type CommitPoint, type FailureDetails, type MaterializedTaskInput, type StageRunState, type StageRunStatus, type TaskResetMetadata, type WorkSourceState } from './types';

export class StageRun {
  readonly tasks: TaskRun[];
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
    this.tasks = definition.tasks.map((task, index) => {
      const taskRun = new TaskRun(task.id, task.title, index, task.uses);
      taskRun.dependsOn = [...(task.dependsOn ?? [])];
      return taskRun;
    });
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
        existing.dependsOn = [...(task.dependsOn ?? existing.dependsOn)];
        continue;
      }
      const taskRun = new TaskRun(task.id, task.title, task.order ?? this.tasks.length, task.uses);
      taskRun.dependsOn = [...(task.dependsOn ?? [])];
      this.tasks.push(taskRun);
    }
    this.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
  }

  nextTask(): TaskRun | null {
    return this.currentTasks().find(task => {
      if (task.terminal) return false;
      return task.dependsOn.every(depId => this.tasks.find(dep => dep.id === depId)?.succeeded);
    }) ?? null;
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
    return this.currentTasks().every(task => task.terminal);
  }

  allRequiredTasksSucceeded(): boolean {
    return this.currentTasks().every(task => task.status === 'completed');
  }

  hasFailedTask(): boolean {
    return this.currentTasks().some(task => task.status === 'failed' || task.status === 'skipped');
  }

  currentTasks(): TaskRun[] {
    const latestByBaseTaskId = new Map<string, TaskRun>();
    for (const task of this.tasks) {
      const baseTaskId = baseRuntimeTaskId(task.id);
      const existing = latestByBaseTaskId.get(baseTaskId);
      if (!existing || task.order > existing.order || (task.order === existing.order && task.id.localeCompare(existing.id) > 0)) {
        latestByBaseTaskId.set(baseTaskId, task);
      }
    }
    return this.tasks.filter(task => latestByBaseTaskId.get(baseRuntimeTaskId(task.id)) === task);
  }

  appendTaskRun(taskId: string, resetBy: TaskResetMetadata): TaskRun {
    const baseTaskId = baseRuntimeTaskId(taskId);
    const latest = this.latestTaskRun(baseTaskId);
    if (!latest) throw new WorkflowDomainError(`Task ${taskId} does not exist in stage ${this.stage}`);
    const nextIndex = this.nextTaskRunIndex(baseTaskId);
    const id = `${baseTaskId}:${nextIndex}`;
    const task = new TaskRun(id, latest.title, latest.order + 1, latest.uses);
    task.dependsOn = [...latest.dependsOn];
    task.resetBy = resetBy;
    this.tasks.push(task);
    this.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
    return task;
  }

  private latestTaskRun(baseTaskId: string): TaskRun | undefined {
    return this.tasks
      .filter(task => baseRuntimeTaskId(task.id) === baseTaskId)
      .sort((a, b) => b.order - a.order || b.id.localeCompare(a.id))[0];
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

  findTask(taskId: string): TaskRun {
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

  resetTask(taskId: string, resetBy: TaskResetMetadata | null = null): void {
    const task = this.findTask(taskId);
    task.resetForFreshAttempt(resetBy);
  }

  resetCheck(checkName: string): void {
    const check = this.findCheck(checkName);
    check.resetForFreshAttempt();
  }

  resetTaskAndDownstream(taskId: string): void {
    const task = this.findTask(taskId);
    const boundaryOrder = task.order;

    for (const t of this.tasks) {
      if (t.order >= boundaryOrder) {
        t.resetForFreshAttempt();
      }
    }
  }

  resetCheckAndDownstream(checkName: string): void {
    const boundaryIndex = this.checks.findIndex(c => c.name === checkName);

    for (const [index, c] of this.checks.entries()) {
      if (index >= boundaryIndex) {
        c.resetForFreshAttempt();
      }
    }

    for (const t of this.tasks) {
      if (t.causedBy?.type === 'check-failure' && t.causedBy.checkName === checkName) {
        t.resetForFreshAttempt();
      }
    }
  }

  scheduledRetryTaskCount(checkName: string): number {
    return this.tasks.filter(task => task.causedBy?.type === 'check-failure' && task.causedBy.checkName === checkName).length;
  }

  appendRetryTask(policy: CheckFailurePolicy, causedBy: CausedByMetadata): TaskRun {
    const suffix = this.scheduledRetryTaskCount(policy.checkName);
    const id = this.tasks.some(task => task.id === policy.retryTaskId) ? `${policy.retryTaskId}:${suffix}` : policy.retryTaskId;
    const taskDefinition = this.retryTaskDefinition(policy.retryTaskId);
    const task = new TaskRun(id, policy.retryTaskTitle, this.tasks.length, taskDefinition?.uses);
    task.reason = causedBy.message ?? `Retry after ${policy.checkName}`;
    task.causedBy = causedBy;
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

  appendAdHocTask(id: string, title: string, causedBy: CausedByMetadata, uses?: string): TaskRun {
    const task = new TaskRun(id, title, this.tasks.length, uses);
    task.reason = causedBy.message ?? title;
    task.causedBy = causedBy;
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
      const isRuntimeTask = !staticTaskIds.has(task.id) && task.causedBy !== null;
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

  restoreTaskState(id: string, title: string, order: number, uses?: string): TaskRun {
    const existing = this.tasks.find(task => task.id === id);
    if (existing) return existing;
    const task = new TaskRun(id, title, order, uses);
    this.tasks.push(task);
    this.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
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
      tasks: this.tasks.map(task => task.state()),
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
}
