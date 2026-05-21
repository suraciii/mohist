import { getWorkflowUseDefinition, validateWorkflowUseEvidence } from '../../uses-catalog';
import {
  cloneResolvedWorkflowDefinition,
  createResolvedWorkflowDefinition,
  type CheckDefinition,
  type CompiledStageDefinition,
  type TaskDefinition,
  type WorkflowStageId,
  type ResolvedWorkflowDefinition,
  type WorkflowTasksFromDefinition,
} from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import {
  baseRuntimeTaskId,
  type ApprovalInput,
  type CausedByMetadata,
  type CheckResultInput,
  type CheckRunStatus,
  type FailureDetails,
  type MaterializedTaskInput,
  type StageCompletionGuard,
  type TaskResultInput,
  type WorkflowDecision,
  type WorkflowEvent,
  type WorkflowRecoverySummary,
  type WorkflowRunState,
  type WorkflowRunStatus,
  type WorkflowWork,
  type WorkItemAttempt,
  type WorkSourceState,
} from './types';

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  status: WorkflowRunStatus = 'running';
  currentStage: WorkflowStageId;
  failure: FailureDetails | null = null;

  private constructor(
    readonly id: string,
    readonly issueId: string,
    readonly issueNumber: number,
    readonly definitions: CompiledStageDefinition[],
    readonly definition: ResolvedWorkflowDefinition,
  ) {
    if (definitions.length === 0) throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    this.stageRuns = definitions.map((definition, index) => new StageRun(definition, index));
    this.currentStage = definitions[0].stage;
  }

  static startWorkflow(input: {
    id: string;
    issueId: string;
    issueNumber: number;
    definitions?: CompiledStageDefinition[];
    definition?: ResolvedWorkflowDefinition;
    now?: string;
  }): { run: WorkflowRun; decision: WorkflowDecision } {
    const definition = input.definition
      ? cloneResolvedWorkflowDefinition(input.definition)
      : input.definitions
        ? createResolvedWorkflowDefinition({
          definition: {
            id: 'runtime/custom',
            name: 'Runtime custom workflow',
            stages: input.definitions,
          },
          source: { type: 'runtime', id: 'runtime/custom' },
          capturedAt: input.now,
        })
        : null;
    if (!definition) {
      throw new WorkflowDomainError('WorkflowRun requires a workflow definition');
    }
    const run = new WorkflowRun(
      input.id,
      input.issueId,
      input.issueNumber,
      input.definitions ?? definition.compiledStageDefinitions,
      definition,
    );
    const firstStage = run.currentStageRun();
    firstStage.start();
    return {
      run,
      decision: run.decision([
        { type: 'workflow-started', stage: firstStage.stage },
        { type: 'stage-started', stage: firstStage.stage },
      ]),
    };
  }

  get stageOrder(): WorkflowStageId[] {
    return this.definitions.map(definition => definition.stage);
  }

  currentStageRun(): StageRun {
    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) throw new WorkflowDomainError(`Current stage ${this.currentStage} is not admitted by this workflow`);
    return stageRun;
  }

  stageRun(stage: WorkflowStageId): StageRun {
    const stageRun = this.stageRuns.find(candidate => candidate.stage === stage);
    if (!stageRun) throw new WorkflowDomainError(`Stage ${stage} is not admitted by this workflow`);
    return stageRun;
  }

  tasksFromDefinition(stage: WorkflowStageId): WorkflowTasksFromDefinition | null {
    const source = this.definitions.find(definition => definition.stage === stage)?.tasksFrom;
    if (!source) return null;
    if (typeof source === 'string') return { uses: source };
    return {
      uses: source.uses,
      with: source.with ? { ...source.with } : undefined,
    };
  }

  taskDefinition(stage: WorkflowStageId, taskId: string): TaskDefinition | null {
    const stageRun = this.stageRun(stage);
    const baseTaskId = baseRuntimeTaskId(taskId);
    const task = stageRun.definition.tasks.find(candidate => candidate.id === taskId || candidate.id === baseTaskId)
      ?? stageRun.definition.checks
        .map(check => check.onFailure?.retry?.task)
        .find((candidate): candidate is NonNullable<typeof candidate> => Boolean(candidate && (candidate.id === taskId || candidate.id === baseTaskId)));
    if (!task) return null;
    return {
      ...task,
      id: taskId,
      with: task.with ? { ...task.with } : undefined,
      dependsOn: task.dependsOn ? [...task.dependsOn] : undefined,
      onSuccess: task.onSuccess ? {
        emit: task.onSuccess.emit ? [...task.onSuccess.emit] : undefined,
      } : undefined,
    };
  }

  checkDefinition(stage: WorkflowStageId, checkName: string): CheckDefinition | null {
    const check = this.stageRun(stage).definition.checks.find(candidate => candidate.name === checkName);
    if (!check) return null;
    return {
      ...check,
      with: check.with ? { ...check.with } : undefined,
    };
  }

  materializeTasks(stage: WorkflowStageId, tasks: MaterializedTaskInput[], workSourceState?: 'missing' | 'invalid' | 'empty'): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    stageRun.materializeTasks(tasks);
    if (stageRun.hasDynamicWorkSource()) {
      if (workSourceState === 'missing') {
        stageRun.recordWorkSourceMissing();
      } else if (workSourceState === 'invalid') {
        stageRun.recordWorkSourceInvalid();
      } else if (workSourceState === 'empty') {
        stageRun.recordWorkSourceEmpty();
      } else if (tasks.length === 0) {
        stageRun.recordWorkSourceEmpty();
      } else {
        stageRun.recordWorkSourceEvaluated(tasks);
      }
    }
    return this.decision([]);
  }

  scheduleRuntimeTask(input: {
    taskId: string;
    title: string;
    uses?: string;
    causedBy: CausedByMetadata;
  }): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.currentStageRun();
    if (stageRun.stage !== this.currentStage) {
      throw new WorkflowDomainError(`Cannot schedule runtime task in stage ${stageRun.stage}; current stage is ${this.currentStage}`);
    }

    const existingTask = stageRun.tasks.find(t => t.id === input.taskId && !t.terminal);
    if (existingTask) {
      return this.decision([]);
    }

    if (stageRun.status === 'awaiting-approval') {
      stageRun.status = 'running';
    }

    stageRun.appendAdHocTask(input.taskId, input.title, input.causedBy, input.uses);
    return this.decision([]);
  }

  completeTask(stage: WorkflowStageId, taskId: string, result: TaskResultInput): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);

    const task = stageRun.findTask(taskId);
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) {
      throw new WorkflowDomainError(`Task ${taskId} cannot complete before earlier checks pass`);
    }
    const expected = stageRun.nextTask();
    if (!expected || expected.id !== task.id) {
      throw new WorkflowDomainError(`Task ${taskId} cannot complete before earlier tasks are terminal`);
    }

    const evidenceFailure = result.status === 'completed'
      ? this.workflowUseEvidenceFailure(this.taskUse(stageRun, taskId), result.output)
      : null;
    const effectiveResult: TaskResultInput = evidenceFailure
      ? {
        ...result,
        status: 'failed',
        reason: evidenceFailure,
        causedBy: result.causedBy ?? { type: 'system-policy', taskId, message: evidenceFailure },
      }
      : result;
    if (effectiveResult.status === 'completed') {
      effectiveResult.events = this.mergeTaskEvents(effectiveResult.events, this.taskSuccessEvents(stageRun, taskId));
    }

    task.status = effectiveResult.status;
    task.attempts = effectiveResult.attempts ?? task.attempts + 1;
    task.duration = effectiveResult.duration ?? task.duration;
    task.artifacts = effectiveResult.artifacts ?? task.artifacts;
    task.events = effectiveResult.events ?? task.events;
    task.output = effectiveResult.output ?? task.output;
    task.reason = effectiveResult.reason ?? task.reason;
    task.causedBy = effectiveResult.causedBy ?? task.causedBy;
    task.resetBy = null;

    if (task.latestAttempt?.state === 'running') {
      const attemptNow = new Date().toISOString();
      if (effectiveResult.status === 'completed') {
        task.completeWorkAttempt({ output: effectiveResult.output, artifacts: effectiveResult.artifacts, events: effectiveResult.events, duration: effectiveResult.duration }, attemptNow);
      } else if (effectiveResult.status === 'failed' || effectiveResult.status === 'skipped') {
        task.failWorkAttempt(effectiveResult.reason ?? 'Task failed', null, attemptNow);
      }
    }

    if (this.taskLocksCode(stageRun, taskId) && effectiveResult.status === 'completed') {
      stageRun.commitPoint = {
        taskId,
        uses: this.taskUse(stageRun, taskId),
        metadata: this.extractCommitMetadata(effectiveResult.output),
        createdAt: new Date().toISOString(),
      };
    }

    if (effectiveResult.status === 'failed' || effectiveResult.status === 'skipped') {
      const failure: FailureDetails = {
        reason: 'task-failed',
        stage,
        taskId,
        message: effectiveResult.reason,
        causedBy: effectiveResult.causedBy,
      };
      return this.fail(stageRun, failure, [
        { type: 'task-failed', stage, taskId, reason: failure },
      ]);
    }

    const events: WorkflowEvent[] = [{ type: 'task-completed', stage, taskId }];
    const invalidationEvents = this.applyTaskCompletionInvalidation(stageRun, taskId, effectiveResult);
    events.push(...invalidationEvents);
    if (stageRun.commitPoint) events.push({ type: 'commit-point-created', stage, commitPoint: stageRun.commitPoint });
    return this.maybeCompleteStage(stageRun, events);
  }

  recordCheckResult(stage: WorkflowStageId, result: CheckResultInput): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'running') throw new WorkflowDomainError(`Stage ${stage} is not running`);
    const checkPhase = stageRun.checkPhase(result.name);
    if (checkPhase === 'approval') throw new WorkflowDomainError(`Check ${result.name} is an approval check`);
    if (checkPhase === 'post-task' && !stageRun.allRequiredTasksTerminal()) throw new WorkflowDomainError(`Stage ${stage} cannot run checks before tasks are terminal`);
    if (checkPhase === 'post-task' && !stageRun.allRequiredTasksSucceeded()) throw new WorkflowDomainError(`Stage ${stage} has failed tasks`);
    if (checkPhase === 'pre-task' && stageRun.hasFailedTask()) throw new WorkflowDomainError(`Stage ${stage} has failed tasks`);

    const check = stageRun.findCheck(result.name);
    const expected = stageRun.nextCheck(checkPhase);
    if (!expected || expected.name !== check.name) {
      throw new WorkflowDomainError(`Check ${result.name} cannot run before earlier checks pass`);
    }

    const normalizedOutput = result.output ?? null;
    const evidenceFailure = result.status === 'pass'
      ? this.workflowUseEvidenceFailure(this.checkUse(stageRun, result.name), normalizedOutput)
      : null;
    const effectiveStatus: CheckResultInput['status'] = evidenceFailure ? 'fail' : result.status;
    const effectiveMessage = evidenceFailure ?? result.message;

    check.status = this.toCheckStatus(effectiveStatus);
    check.message = effectiveMessage ?? null;
    check.output = normalizedOutput;
    if (effectiveStatus !== 'pending') check.runCount += 1;

    if (check.latestAttempt?.state === 'running') {
      const attemptNow = new Date().toISOString();
      if (effectiveStatus === 'pass') {
        check.completeWorkAttempt(attemptNow);
      } else if (effectiveStatus === 'fail' || effectiveStatus === 'error') {
        check.failWorkAttempt(effectiveMessage ?? `Check ${result.name} failed`, null, attemptNow);
      }
    }

    if (this.checkLocksCode(stageRun, check.name) && effectiveStatus === 'pass') {
      stageRun.commitPoint = {
        checkName: check.name,
        uses: this.checkUse(stageRun, check.name),
        metadata: this.extractCommitMetadata(normalizedOutput),
        createdAt: new Date().toISOString(),
      };
    }

    const events: WorkflowEvent[] = [{ type: 'check-recorded', stage, checkName: check.name, status: check.status }];
    if (effectiveStatus === 'pending' || effectiveStatus === 'pass') {
      if (stageRun.commitPoint) events.push({ type: 'commit-point-created', stage, commitPoint: stageRun.commitPoint });
      return this.maybeCompleteStage(stageRun, events);
    }

    if (stageRun.commitPoint) {
      return this.fail(stageRun, {
        reason: 'post-commit-check-failed',
        stage,
        checkName: result.name,
        message: effectiveMessage,
      }, events);
    }

    const policy = stageRun.definition.checkFailurePolicies?.find(candidate => candidate.checkName === result.name);
    const scheduledRetryTaskCount = stageRun.scheduledRetryTaskCount(result.name);
    if (policy && scheduledRetryTaskCount < policy.maxAttempts) {
      const causedBy: CausedByMetadata = {
        type: 'check-failure',
        checkName: result.name,
        message: effectiveMessage,
      };
      const retryTask = stageRun.appendRetryTask(policy, causedBy);
      check.status = 'pending';
      events.push({ type: 'retry-task-scheduled', stage, taskId: retryTask.id, causedBy });
      return this.decision(events);
    }

    return this.fail(stageRun, {
      reason: 'check-unrepaired',
      stage,
      checkName: result.name,
      message: effectiveMessage,
      causedBy: { type: 'check-failure', checkName: result.name, message: effectiveMessage },
    }, events);
  }

  approveStage(stage: WorkflowStageId, input: ApprovalInput = {}): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'awaiting-approval' || !stageRun.approval) {
      throw new WorkflowDomainError(`Stage ${stage} is not awaiting approval`);
    }
    const guard = this.evaluateStageCompletionGuard(stageRun, { includeApproval: false });
    if (!guard.complete) {
      return { events: [], nextWork: { kind: 'blocked', stage, reason: guard } };
    }
    stageRun.approval = {
      ...stageRun.approval,
      status: 'approved',
      output: input.output ?? null,
      respondedAt: new Date().toISOString(),
    };
    return this.completeStage(stageRun, [{ type: 'approval-approved', stage }]);
  }

  rejectStage(stage: WorkflowStageId, input: ApprovalInput = {}): WorkflowDecision {
    this.assertRunning();
    const stageRun = this.assertCurrentStage(stage);
    if (stageRun.status !== 'awaiting-approval' || !stageRun.approval) {
      throw new WorkflowDomainError(`Stage ${stage} is not awaiting approval`);
    }
    stageRun.approval = {
      ...stageRun.approval,
      status: 'rejected',
      output: input.output ?? null,
      respondedAt: new Date().toISOString(),
    };
    const failure: FailureDetails = {
      reason: 'approval-rejected',
      stage,
      message: typeof input.output === 'string' ? input.output : undefined,
    };
    return this.fail(stageRun, failure, [{ type: 'approval-rejected', stage, reason: failure }]);
  }

  startTaskAttempt(stage: WorkflowStageId, taskId: string, now: string, evidence?: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>>): void {
    if (this.status !== 'running') return;
    const stageRun = this.stageRun(stage);
    const task = stageRun.findTask(taskId);
    task.startWorkAttempt(now, evidence);
  }

  startCheckAttempt(stage: WorkflowStageId, checkName: string, now: string, evidence?: Partial<Pick<WorkItemAttempt, 'queueTaskId' | 'acpSessionId' | 'coderSessionId' | 'executionId' | 'processPid'>>): void {
    if (this.status !== 'running') return;
    const stageRun = this.stageRun(stage);
    const check = stageRun.findCheck(checkName);
    check.startWorkAttempt(now, evidence);
  }

  interruptSpecificWorkAttempts(attempts: WorkItemAttempt[], reason: string, diagnostic: string | null = null): void {
    if (attempts.length === 0) return;
    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) return;
    const now = new Date().toISOString();
    const pending = new Set(attempts);
    let interrupted = 0;

    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running' && pending.has(task.latestAttempt)) {
        task.interruptWorkAttempt(reason, diagnostic, now);
        pending.delete(task.latestAttempt);
        interrupted++;
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running' && pending.has(check.latestAttempt)) {
        check.interruptWorkAttempt(reason, diagnostic, now);
        pending.delete(check.latestAttempt);
        interrupted++;
      }
    }
    if (interrupted > 0) this.markWaitingForRecovery(stageRun, reason, diagnostic);
  }

  interruptRunningWorkAttempts(reason: string, diagnostic: string | null = null): void {
    if (this.status !== 'running') return;
    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) return;
    const now = new Date().toISOString();
    let interrupted = 0;
    for (const task of stageRun.tasks) {
      if (task.latestAttempt?.state === 'running') {
        task.interruptWorkAttempt(reason, diagnostic, now);
        interrupted++;
      }
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running') {
        check.interruptWorkAttempt(reason, diagnostic, now);
        interrupted++;
      }
    }
    if (interrupted > 0) this.markWaitingForRecovery(stageRun, reason, diagnostic);
  }

  retryStage(stage: WorkflowStageId): WorkflowDecision {
    if (this.status !== 'failed') {
      throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
    }
    const stageRun = this.stageRun(stage);
    if (this.currentStage !== stage) {
      throw new WorkflowDomainError(`Stage ${stage} is not current stage ${this.currentStage}`);
    }
    if (stageRun.status !== 'failed') {
      throw new WorkflowDomainError(`Stage ${stage} is not failed`);
    }

    const stageFailureReason = stageRun.failure?.reason;
    const runFailureReason = this.failure?.reason;

    this.status = 'running';
    this.failure = null;

    for (const priorStageRun of this.stageRuns) {
      if (priorStageRun.order >= stageRun.order) break;
      if (priorStageRun.status !== 'passed') continue;
      for (const task of priorStageRun.tasks) {
        if (task.status === 'completed') continue;
        task.status = 'completed';
        if (task.attempts === 0) task.attempts = 1;
        task.reason = null;
        task.causedBy = null;
      }
    }

    stageRun.status = 'running';
    stageRun.attemptSequence += 1;
    const wasApprovalRejected = (stageFailureReason ?? runFailureReason) === 'approval-rejected';
    const failedTask = stageRun.tasks.find(t => t.status === 'failed' || t.status === 'skipped');
    const failedCheck = stageRun.checks.find(c => c.status === 'failed' || c.status === 'error');
    stageRun.failure = null;
    stageRun.approval = null;

    if (wasApprovalRejected) {
      for (const task of stageRun.tasks) {
        task.resetForFreshAttempt();
      }
      for (const check of stageRun.checks) {
        check.resetForFreshAttempt();
      }
    } else {
      if (failedTask) {
        stageRun.resetTaskAndDownstream(failedTask.id);
        for (const check of stageRun.checks) {
          check.resetForFreshAttempt();
        }
      } else if (failedCheck) {
        const retryInvalidationEvents = this.applyRetryInvalidationForCompletedTasks(stageRun);
        if (retryInvalidationEvents.length > 0) {
          return this.decision([
            { type: 'stage-retried', stage },
            ...retryInvalidationEvents,
          ]);
        }

        for (const task of stageRun.tasks) {
          if (!task.terminal) {
            const isRetryTaskForFailedCheck = task.causedBy?.type === 'check-failure' && task.causedBy.checkName === failedCheck.name;
            if (!isRetryTaskForFailedCheck) {
              task.resetForFreshAttempt();
            }
          }
        }
        stageRun.resetCheckAndDownstream(failedCheck.name);
      } else {
        for (const task of stageRun.tasks) {
          if (!task.terminal) {
            task.resetForFreshAttempt();
          }
        }
        for (const check of stageRun.checks) {
          check.resetForFreshAttempt();
        }
      }
    }

    return this.decision([{ type: 'stage-retried', stage }]);
  }

  private applyRetryInvalidationForCompletedTasks(stageRun: StageRun): WorkflowEvent[] {
    const policy = stageRun.definition.invalidationPolicy;
    if (!policy) return [];

    const events: WorkflowEvent[] = [];
    for (const task of stageRun.tasks) {
      if (task.status !== 'completed') continue;
      const baseTaskId = baseRuntimeTaskId(task.id);
      const raisedEvents = new Set(task.events);
      for (const entry of policy.entries) {
        if (entry.trigger !== 'task-completion') continue;
        if (entry.triggerTaskId && entry.triggerTaskId !== task.id && entry.triggerTaskId !== baseTaskId) continue;
        if (entry.eventName && !raisedEvents.has(entry.eventName)) continue;
        const reason = entry.reason ?? `Policy invalidation while retrying after ${task.id}`;
        for (const taskId of entry.invalidates.tasks ?? []) {
          try {
            const newTaskRun = stageRun.appendTaskRun(taskId, {
              type: 'workflow-policy',
              taskId: task.id,
              eventName: entry.eventName,
              message: reason,
            });
            events.push({ type: 'task-invalidated', stage: stageRun.stage, taskId: newTaskRun.id, reason });
          } catch {
            // Task may not belong to this stage definition.
          }
        }
        for (const checkName of entry.invalidates.checks ?? []) {
          try {
            stageRun.resetCheck(checkName);
            events.push({ type: 'check-invalidated', stage: stageRun.stage, checkName, reason });
          } catch {
            // Check may not belong to this stage definition.
          }
        }
        if (entry.invalidates.approval && stageRun.approval) {
          stageRun.approval = null;
          if (stageRun.status === 'awaiting-approval') {
            stageRun.status = 'running';
          }
        }
      }
    }
    return events;
  }

  canRetryStage(stage: WorkflowStageId): boolean {
    if (this.status !== 'failed') return false;
    if (this.currentStage !== stage) return false;
    const stageRun = this.stageRuns.find(candidate => candidate.stage === stage);
    if (!stageRun) return false;
    if (stageRun.status !== 'failed') return false;
    if (this.findCurrentStageInterruptedAttempt(stageRun)) return false;
    if ((stageRun.failure?.reason ?? this.failure?.reason) === 'approval-rejected') return true;
    return this.findCurrentStageFailedAttempt(stageRun) !== null;
  }

  rerunStage(stage: WorkflowStageId): WorkflowDecision {
    const stageRun = this.assertCurrentStage(stage);

    this.status = 'running';
    this.failure = null;

    for (const priorStageRun of this.stageRuns) {
      if (priorStageRun.order >= stageRun.order) break;
      if (priorStageRun.status !== 'passed') continue;
      for (const task of priorStageRun.tasks) {
        if (task.status === 'completed') continue;
        task.status = 'completed';
        if (task.attempts === 0) task.attempts = 1;
        task.reason = null;
        task.causedBy = null;
      }
    }

    stageRun.status = 'running';
    stageRun.attemptSequence += 1;
    stageRun.failure = null;
    stageRun.approval = null;

    stageRun.removeGeneratedTasks();
    if (stageRun.hasDynamicWorkSource()) {
      stageRun.removeNonStaticTasks();
      stageRun.resetWorkSourceState();
    }

    for (const task of stageRun.tasks) {
      task.resetForFreshAttempt();
    }
    for (const check of stageRun.checks) {
      check.resetForFreshAttempt();
    }

    return this.decision([{ type: 'stage-retried', stage }]);
  }

  nextWork(): WorkflowWork {
    if (this.status === 'passed') return { kind: 'complete' };
    if (this.status === 'failed') return { kind: 'failed', reason: this.failure! };
    const stageRun = this.currentStageRun();
    const failedTask = stageRun.currentTasks().find(task => task.status === 'failed' || task.status === 'skipped');
    if (failedTask) {
      const failure: FailureDetails = {
        reason: 'task-failed',
        stage: stageRun.stage,
        taskId: failedTask.id,
        message: failedTask.reason ?? undefined,
        causedBy: failedTask.causedBy ?? undefined,
      };
      this.fail(stageRun, failure, []);
      return { kind: 'failed', reason: failure };
    }
    if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval', stage: stageRun.stage };
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) return { kind: 'check', stage: stageRun.stage, checkName: preTaskCheck.name };
    const task = stageRun.nextTask();
    if (task) return { kind: 'task', stage: stageRun.stage, taskId: task.id };
    const workSourceFailure = this.evaluateWorkSourceFailureGuard(stageRun);
    if (workSourceFailure) return { kind: 'blocked', stage: stageRun.stage, reason: workSourceFailure };
    const check = stageRun.nextCheck('post-task');
    if (check) return { kind: 'check', stage: stageRun.stage, checkName: check.name };
    const guard = this.evaluateStageCompletionGuard(stageRun);
    if (!guard.complete) return { kind: 'blocked', stage: stageRun.stage, reason: guard };
    return { kind: 'complete' };
  }

  state(): WorkflowRunState {
    return {
      id: this.id,
      issueId: this.issueId,
      issueNumber: this.issueNumber,
      status: this.status,
      currentStage: this.currentStage,
      stageOrder: this.stageOrder,
      stageRuns: this.stageRuns.map(stageRun => stageRun.state()),
      failure: this.failure,
    };
  }

  workflowRecoverySummary(): WorkflowRecoverySummary {
    if (this.status === 'passed') return 'completed';

    const stageRun = this.currentStageRun();
    if (!stageRun) {
      return this.status === 'failed' ? 'waiting-for-recovery' : 'running';
    }

    if (stageRun.status === 'awaiting-approval') return 'awaiting-approval';

    const latestRunningAttempt = this.findCurrentStageRunningAttempt(stageRun);
    if (latestRunningAttempt) return 'running';

    const failedTask = stageRun.currentTasks().find(t => t.status === 'failed' || t.status === 'skipped');
    const failedCheck = stageRun.checks.find(c => c.status === 'failed' || c.status === 'error');
    if (failedTask || failedCheck) return 'waiting-for-recovery';

    const interruptedAttempt = this.findCurrentStageInterruptedAttempt(stageRun);
    if (interruptedAttempt) return 'waiting-for-recovery';

    const currentWorkItem = this.findCurrentStagePendingWorkItem(stageRun);
    if (currentWorkItem?.latestAttempt?.state === 'failed') return 'waiting-for-recovery';
    if (currentWorkItem?.latestAttempt === null && currentWorkItem.causedBy) return 'waiting-for-recovery';

    if (this.status === 'failed') return 'waiting-for-recovery';

    return 'running';
  }

  private findCurrentStagePendingWorkItem(stageRun: StageRun): {
    latestAttempt: WorkItemAttempt | null;
    causedBy?: CausedByMetadata | null;
  } | null {
    const preTaskCheck = stageRun.nextCheck('pre-task');
    if (preTaskCheck) return { latestAttempt: preTaskCheck.latestAttempt };
    const task = stageRun.nextTask();
    if (task) return { latestAttempt: task.latestAttempt, causedBy: task.causedBy };
    const postTaskCheck = stageRun.nextCheck('post-task');
    if (postTaskCheck) return { latestAttempt: postTaskCheck.latestAttempt };
    return null;
  }

  private findCurrentStageRunningAttempt(stageRun: StageRun): WorkItemAttempt | null {
    for (const task of stageRun.currentTasks()) {
      if (task.latestAttempt?.state === 'running') return task.latestAttempt;
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'running') return check.latestAttempt;
    }
    return null;
  }

  private findCurrentStageInterruptedAttempt(stageRun: StageRun): WorkItemAttempt | null {
    for (const task of stageRun.currentTasks()) {
      if (task.latestAttempt?.state === 'interrupted') return task.latestAttempt;
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'interrupted') return check.latestAttempt;
    }
    return null;
  }

  private findCurrentStageFailedAttempt(stageRun: StageRun): WorkItemAttempt | null {
    for (const task of stageRun.currentTasks()) {
      if (task.latestAttempt?.state === 'failed') return task.latestAttempt;
    }
    for (const check of stageRun.checks) {
      if (check.latestAttempt?.state === 'failed') return check.latestAttempt;
    }
    return null;
  }

  private assertRunning(): void {
    if (this.status !== 'running') throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
  }

  private assertCurrentStage(stage: WorkflowStageId): StageRun {
    const stageRun = this.stageRun(stage);
    if (stageRun.stage !== this.currentStage) {
      throw new WorkflowDomainError(`Stage ${stage} is not current stage ${this.currentStage}`);
    }
    return stageRun;
  }

  private maybeCompleteStage(stageRun: StageRun, events: WorkflowEvent[]): WorkflowDecision {
    const guard = this.evaluateStageCompletionGuard(stageRun, { includeApproval: false });
    if (!guard.complete) return this.decision(events);
    if (stageRun.requiresApproval() && stageRun.approval?.status !== 'approved') {
      if (!stageRun.approval) {
        stageRun.requestApproval(new Date().toISOString(), this.buildApprovalOutput(stageRun));
        events.push({ type: 'approval-requested', stage: stageRun.stage });
      } else if (stageRun.approval.status === 'awaiting') {
        stageRun.status = 'awaiting-approval';
      }
      return this.decision(events);
    }
    return this.completeStage(stageRun, events);
  }

  private evaluateWorkSourceFailureGuard(stageRun: StageRun): StageCompletionGuard | null {
    if (!stageRun.hasDynamicWorkSource()) return null;
    const state: WorkSourceState = stageRun.workSourceState;
    if (!state.evaluated) return { complete: false, reason: 'dynamic-source-not-evaluated', stage: stageRun.stage };
    if ('missing' in state && state.missing) return { complete: false, reason: 'dynamic-source-missing', stage: stageRun.stage };
    if ('invalid' in state && state.invalid) return { complete: false, reason: 'dynamic-source-invalid', stage: stageRun.stage };
    if ('empty' in state && state.empty) return { complete: false, reason: 'dynamic-source-empty', stage: stageRun.stage };
    return null;
  }

  private evaluateStageCompletionGuard(
    stageRun: StageRun,
    options: { includeApproval?: boolean } = {},
  ): StageCompletionGuard {
    for (const taskDef of stageRun.definition.tasks) {
      const taskRun = stageRun.currentTasks().find(t => baseRuntimeTaskId(t.id) === taskDef.id);
      if (!taskRun) return { complete: false, reason: 'missing-static-task', taskId: taskDef.id };
      if (taskRun.status !== 'completed') return { complete: false, reason: 'static-task-not-successful', taskId: taskDef.id, status: taskRun.status };
    }

    const nonApprovalCheckNames = new Set(stageRun.nonApprovalCheckPolicies().map(policy => policy.checkName));
    for (const checkDef of stageRun.definition.checks) {
      if (!nonApprovalCheckNames.has(checkDef.name)) continue;
      const checkRun = stageRun.checks.find(c => c.name === checkDef.name);
      if (!checkRun) return { complete: false, reason: 'missing-static-check', checkName: checkDef.name };
      if (checkRun.status !== 'passed') return { complete: false, reason: 'static-check-not-passed', checkName: checkDef.name };
    }

    const workSourceGuard = this.evaluateWorkSourceFailureGuard(stageRun);
    if (workSourceGuard) return workSourceGuard;

    const deliveryEvidenceGuard = this.evaluateDeliveryEvidenceGuard(stageRun);
    if (!deliveryEvidenceGuard.complete) return deliveryEvidenceGuard;

    for (const taskRun of stageRun.currentTasks()) {
      if (!taskRun.terminal) return { complete: false, reason: 'run-task-pending', taskId: taskRun.id };
    }

    if ((options.includeApproval ?? true) && stageRun.requiresApproval() && stageRun.approval?.status !== 'approved') {
      return { complete: false, reason: 'approval-required', stage: stageRun.stage };
    }

    return { complete: true };
  }

  private evaluateDeliveryEvidenceGuard(stageRun: StageRun): StageCompletionGuard {
    for (const taskRun of stageRun.currentTasks()) {
      if (taskRun.status !== 'completed') continue;
      const uses = this.taskUse(stageRun, taskRun.id);
      const evidence = validateWorkflowUseEvidence(uses, taskRun.output);
      if (!evidence.ok) {
        return { complete: false, reason: 'commit-evidence-missing', stage: stageRun.stage, taskId: taskRun.id, uses };
      }
    }
    for (const checkRun of stageRun.checks) {
      if (checkRun.status !== 'passed') continue;
      const uses = this.checkUse(stageRun, checkRun.name);
      const evidence = validateWorkflowUseEvidence(uses, checkRun.output);
      if (!evidence.ok) {
        return { complete: false, reason: 'commit-evidence-missing', stage: stageRun.stage, checkName: checkRun.name, uses };
      }
    }
    return { complete: true };
  }

  private workflowUseEvidenceFailure(uses: string | undefined, output: unknown): string | null {
    const evidence = validateWorkflowUseEvidence(uses, output);
    if (evidence.ok) return null;
    if (evidence.reason === 'unknown-use') return `Unknown workflow use ${uses ?? '<unspecified>'}`;
    return `Missing required evidence for ${uses}: ${evidence.field ?? 'output'}`;
  }

  private taskUse(stageRun: StageRun, taskId: string): string | undefined {
    const baseTaskId = baseRuntimeTaskId(taskId);
    const taskRun = stageRun.tasks.find(task => task.id === taskId || task.id === baseTaskId);
    const taskDefinition = stageRun.definition.tasks.find(task => task.id === taskId || task.id === baseTaskId)
      ?? stageRun.definition.checks
        .map(check => check.onFailure?.retry?.task)
        .find((task): task is NonNullable<typeof task> => Boolean(task && (task.id === taskId || task.id === baseTaskId)));
    return taskRun?.uses ?? taskDefinition?.uses;
  }

  private taskLocksCode(stageRun: StageRun, taskId: string): boolean {
    const uses = this.taskUse(stageRun, taskId);
    if (!uses) return false;
    const use = getWorkflowUseDefinition(uses);
    return use?.createsCommitPoint === true;
  }

  private checkUse(stageRun: StageRun, checkName: string): string | undefined {
    const checkDefinition = stageRun.definition.checks.find(check => check.name === checkName);
    return checkDefinition?.uses;
  }

  private checkLocksCode(stageRun: StageRun, checkName: string): boolean {
    const uses = this.checkUse(stageRun, checkName);
    if (!uses) return false;
    const use = getWorkflowUseDefinition(uses);
    return use?.createsCommitPoint === true;
  }

  private completeStage(stageRun: StageRun, events: WorkflowEvent[]): WorkflowDecision {
    const guard = this.evaluateStageCompletionGuard(stageRun);
    if (!guard.complete) return this.decision(events);

    stageRun.status = 'passed';
    events.push({ type: 'stage-completed', stage: stageRun.stage });

    const next = this.stageRuns[stageRun.order + 1];
    if (!next) {
      this.status = 'passed';
      events.push({ type: 'workflow-completed' });
      return this.decision(events);
    }

    if (next.status !== 'pending') throw new WorkflowDomainError(`Next stage ${next.stage} is not pending`);
    this.currentStage = next.stage;
    next.start();
    events.push({ type: 'stage-started', stage: next.stage });
    return this.decision(events);
  }

  private fail(stageRun: StageRun, failure: FailureDetails, events: WorkflowEvent[]): WorkflowDecision {
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
    events.push({ type: 'stage-failed', stage: stageRun.stage, reason: failure });
    events.push({ type: 'workflow-failed', reason: failure });
    return this.decision(events);
  }

  private markWaitingForRecovery(stageRun: StageRun, reason: string, diagnostic: string | null): void {
    const failure: FailureDetails = {
      reason: 'work-interrupted',
      stage: stageRun.stage,
      message: diagnostic ?? reason,
    };
    stageRun.status = 'failed';
    stageRun.failure = failure;
    this.status = 'failed';
    this.failure = failure;
  }

  private decision(events: WorkflowEvent[]): WorkflowDecision {
    return { events, nextWork: this.nextWork() };
  }

  private buildApprovalOutput(stageRun: StageRun): unknown {
    const approvalCheckName = stageRun.definition.approvalPolicy?.checkName ?? stageRun.definition.approvalCheckName;
    if (!approvalCheckName) return null;

    const passedChecks = stageRun.checks
      .filter(check => check.status === 'passed')
      .map(check => ({
        name: check.name,
        output: check.output,
      }));
    return {
      result: 'PASS',
      checks: passedChecks,
    };
  }

  private toCheckStatus(status: CheckResultInput['status']): CheckRunStatus {
    if (status === 'pass') return 'passed';
    if (status === 'fail') return 'failed';
    return status;
  }

  private applyTaskCompletionInvalidation(stageRun: StageRun, taskId: string, result: TaskResultInput): WorkflowEvent[] {
    const events: WorkflowEvent[] = [];
    const policy = stageRun.definition.invalidationPolicy;
    if (!policy) return events;
    const baseTaskId = baseRuntimeTaskId(taskId);
    const raisedEvents = new Set(result.events ?? []);

    for (const entry of policy.entries) {
      if (entry.trigger !== 'task-completion') continue;
      if (entry.triggerTaskId && entry.triggerTaskId !== taskId && entry.triggerTaskId !== baseTaskId) continue;
      if (entry.eventName && !raisedEvents.has(entry.eventName)) continue;

      if (entry.invalidates.tasks) {
        for (const t of entry.invalidates.tasks) {
          try {
            const reason = entry.reason ?? `Policy invalidation after ${taskId}`;
            const task = stageRun.appendTaskRun(t, {
              type: 'workflow-policy',
              taskId,
              eventName: entry.eventName,
              message: reason,
            });
            events.push({ type: 'task-invalidated', stage: stageRun.stage, taskId: task.id, reason });
          } catch {
            // task not in stage, skip
          }
        }
      }
      if (entry.invalidates.checks) {
        for (const c of entry.invalidates.checks) {
          try {
            stageRun.resetCheck(c);
            const reason = entry.reason ?? `Policy invalidation after ${taskId}`;
            events.push({ type: 'check-invalidated', stage: stageRun.stage, checkName: c, reason });
          } catch {
            // check not in stage, skip
          }
        }
      }
      if (entry.invalidates.approval && stageRun.approval) {
        stageRun.approval = null;
        if (stageRun.status === 'awaiting-approval') {
          stageRun.status = 'running';
        }
      }
    }
    return events;
  }

  private taskSuccessEvents(stageRun: StageRun, taskId: string): string[] {
    const baseTaskId = baseRuntimeTaskId(taskId);
    const taskDefinitions = [
      ...stageRun.definition.tasks,
      ...stageRun.definition.checks.flatMap(check => check.onFailure?.retry?.task ? [check.onFailure.retry.task] : []),
    ];
    for (const task of taskDefinitions) {
      if (task.id === taskId || task.id === baseTaskId) {
        return task.onSuccess?.emit ?? [];
      }
    }
    return [];
  }

  private mergeTaskEvents(resultEvents: string[] | undefined, configuredEvents: string[]): string[] | undefined {
    const merged = new Set<string>(resultEvents ?? []);
    for (const eventName of configuredEvents) {
      merged.add(eventName);
    }
    return merged.size > 0 ? [...merged] : undefined;
  }

  private extractCommitMetadata(output: unknown): Record<string, unknown> {
    const data = this.unwrapTaskOutput(output);
    return data ? { ...data } : {};
  }

  private unwrapTaskOutput(output: unknown): Record<string, unknown> | null {
    if (!output || typeof output !== 'object') return null;
    const data = output as Record<string, unknown>;
    if (data.kind === 'service-call-task' && data.result && typeof data.result === 'object') {
      return data.result as Record<string, unknown>;
    }
    return data;
  }
}
