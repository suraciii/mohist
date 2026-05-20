import { Stage, type Issue } from '../types';
import type { StageRunner } from './stage-runner';
import type { StageContext, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, WorkflowApplicationRuntime, AgentSessionRegistry } from './stage-context';
import { InMemoryAgentSessionRegistry } from './stage-context';
import type { CheckpointManager } from './checkpoint-manager';
import type { EventBus } from '../services/event-bus';
import type { AgentSessionOptions } from '../agent-runtime/agent-session';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { StageExecutionRepo } from '../db/stage-execution-repo';
import type { WorkflowRunService } from '../services/workflow-run-service';
import type { ConfigInfo } from '../config/config-schema';
import type { StageStateService } from '../services/stage-state-service';
import { resolveStageModel } from '../config/model-resolution';
import { createWorkflowSessionObservers } from '../agent-runtime';
import type { StageCompletionGuard, TaskRunSnapshot, WorkflowStageId, WorkflowWork } from './model';

export interface PipelineResult {
  completed: boolean;
  stage: WorkflowStageId;
  message?: string;
}

export interface WorkflowEngineOptions {
  runners: StageRunner[];
  issueRepo: IssueRepo;
  eventBus: EventBus;
  checkpointManager: CheckpointManager;
  artifactManager: ChangeArtifactsManager;
  worktreeManager?: WorktreeManager;
  projectRepo?: ProjectRepo;
  projectId?: string;
  signal?: AbortSignal;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo?: CoderSessionRepo;
  stageExecutionRepo?: StageExecutionRepo;
  stageStateService?: StageStateService;
  workflowRunService?: WorkflowRunService;
  workflowApplicationService?: WorkflowApplicationRuntime;
  config?: ConfigInfo;
}

type StageAttemptRunInfo = {
  id: string;
  stageRuns: Array<{ stage: WorkflowStageId; attemptSequence?: number }>;
};

export class WorkflowEngine {
  private runners: StageRunner[];
  private issueRepo: IssueRepo;
  private eventBus: EventBus;
  private checkpointManager: CheckpointManager;
  private artifactManager: ChangeArtifactsManager;
  private worktreeManager?: WorktreeManager;
  private projectRepo?: ProjectRepo;
  private signal?: AbortSignal;
  private workflowLogRepo?: WorkflowLogRepo;
  private sessionStreamLogRepo?: SessionStreamLogRepo;
  private coderSessionRepo?: CoderSessionRepo;
  private stageExecutionRepo?: StageExecutionRepo;
  private stageStateService?: StageStateService;
  private workflowRunService?: WorkflowRunService;
  private workflowApplicationService?: WorkflowApplicationRuntime;
  private config?: ConfigInfo;
  private pendingRejectionFeedback = new Map<string, unknown>();
  private stageRegistries = new Map<string, AgentSessionRegistry>();

  constructor(options: WorkflowEngineOptions) {
    this.runners = options.runners;
    this.issueRepo = options.issueRepo;
    this.eventBus = options.eventBus;
    this.checkpointManager = options.checkpointManager;
    this.artifactManager = options.artifactManager;
    this.worktreeManager = options.worktreeManager;
    this.projectRepo = options.projectRepo;
    this.signal = options.signal;
    this.workflowLogRepo = options.workflowLogRepo;
    this.sessionStreamLogRepo = options.sessionStreamLogRepo;
    this.coderSessionRepo = options.coderSessionRepo;
    this.stageExecutionRepo = options.stageExecutionRepo;
    this.stageStateService = options.stageStateService;
    this.workflowRunService = options.workflowRunService;
    this.workflowApplicationService = options.workflowApplicationService;
    this.config = options.config;
  }

  private buildContext(issue: Issue, acpOptions: AgentSessionOptions, work?: WorkflowWork): StageContext {
    const stage = work && 'stage' in work ? work.stage : issue.stage;
    const workflowRun = this.workflowRunService ? this.workflowRunService.getActiveRunForIssue(issue.id) ?? undefined : undefined;
    const stageAttemptSequence = workflowRun?.stageRuns.find(candidate => candidate.stage === stage)?.attemptSequence ?? 1;
    const rejectionFeedback = this.pendingRejectionFeedback.get(`${issue.id}:${stage}`);
    const resolvedModel = this.config ? resolveStageModel(stage, this.config, issue) : undefined;
    const wfObservers = createWorkflowSessionObservers({
      eventBus: this.eventBus,
      workflowLogRepo: this.workflowLogRepo,
      sessionStreamLogRepo: this.sessionStreamLogRepo,
      coderSessionRepo: this.coderSessionRepo,
    });
    const emit: StageContext['emit'] = (event, data) => {
      try {
        this.eventBus.emit(event as keyof import('../services/event-bus').EventMap, data as never);
      } catch (e) {
        // fire-and-forget
      }
    };
    const log: StageContext['log'] = (eventType, data) => {
      if (!this.workflowLogRepo) return;
      try {
        this.workflowLogRepo.insert(issue.id, null, eventType, data);
      } catch (e) {
        // fire-and-forget
      }
    };
    const registryKey = workflowRun ? this.stageAttemptKey(workflowRun.id, stage, stageAttemptSequence) : null;
    return {
      issue: { ...issue, stage },
      acpOptions: {
        ...acpOptions,
        signal: this.signal,
        ...(resolvedModel !== undefined ? { model: resolvedModel } : {}),
        observers: wfObservers,
      },
      artifactManager: this.artifactManager,
      worktreeManager: this.worktreeManager as WorktreeManager,
      projectRepo: this.projectRepo as ProjectRepo,
      eventBus: this.eventBus,
      checkpointManager: this.checkpointManager,
      issueRepo: this.issueRepo,
      workflowLogRepo: this.workflowLogRepo,
      sessionStreamLogRepo: this.sessionStreamLogRepo,
      coderSessionRepo: this.coderSessionRepo,
      stageExecutionRepo: this.stageExecutionRepo,
      stageStateService: this.stageStateService,
      workflowRunService: this.workflowRunService,
      workflowApplicationService: this.workflowApplicationService,
      workflowRun,
      requestedWork: work,
      requestedTask: this.findRequestedTask(workflowRun, work),
      rejectionFeedback,
      signal: this.signal,
      agentSessionRegistry: registryKey ? this.getOrCreateStageRegistry(registryKey) : undefined,
      emit,
      log,
    };
  }

  private findRequestedTask(
    run: StageContext['workflowRun'],
    work: WorkflowWork | undefined,
  ): TaskRunSnapshot | undefined {
    if (work?.kind !== 'task') return undefined;
    const stageRun = run?.stageRuns.find(candidate => candidate.stage === work.stage);
    const task = stageRun?.tasks.find(candidate => candidate.taskId === work.taskId);
    if (!task) return undefined;
    return {
      id: task.taskId,
      title: task.title,
      status: task.status,
      order: task.taskOrder,
      dependsOn: [],
      attempts: task.attempts,
      duration: task.duration,
      artifacts: task.artifacts,
      events: [],
      output: task.output,
      reason: task.reason,
      causedBy: task.causedByType
        ? {
            type: task.causedByType as NonNullable<TaskRunSnapshot['causedBy']>['type'],
            checkName: task.causedByCheckName ?? undefined,
            taskId: task.causedByTaskId ?? undefined,
            message: task.reason ?? undefined,
          }
        : null,
      resetBy: task.resetByType
        ? {
            type: task.resetByType as NonNullable<TaskRunSnapshot['resetBy']>['type'],
            taskId: task.resetByTaskId ?? undefined,
            eventName: task.resetByEventName ?? undefined,
            message: task.resetReason ?? undefined,
          }
        : null,
      latestAttempt: null,
    };
  }

  private refreshIssue(issue: Issue): Issue {
    return this.issueRepo.findById(issue.id) ?? issue;
  }

  private getRunner(stage: WorkflowStageId): StageRunner | null {
    return this.runners.find(r => r.canHandle(stage)) ?? null;
  }

  private formatFailure(work: Extract<WorkflowWork, { kind: 'failed' }>): string {
    const reason = work.reason;
    const subject = reason.taskId ?? reason.checkName ?? reason.stage;
    return reason.message ?? `${reason.reason}: ${subject}`;
  }

  private formatBlocked(stage: WorkflowStageId, reason: StageCompletionGuard): string {
    if (reason.complete) return `Blocked at ${stage}`;
    const subject = 'taskId' in reason
      ? reason.taskId
      : 'checkName' in reason
        ? reason.checkName
        : 'stage' in reason
          ? reason.stage
          : stage;
    return `${reason.reason}: ${subject}`;
  }

  private stageAttemptKey(workflowRunId: string, stage: WorkflowStageId, attemptSequence: number): string {
    return `${workflowRunId}:${stage}:${attemptSequence}`;
  }

  private stageAttemptKeyFromRun(
    workflowRun: StageAttemptRunInfo,
    stage: WorkflowStageId,
  ): string {
    const stageAttemptSequence = workflowRun.stageRuns.find(candidate => candidate.stage === stage)?.attemptSequence ?? 1;
    return this.stageAttemptKey(workflowRun.id, stage, stageAttemptSequence);
  }

  private getOrCreateStageRegistry(key: string): AgentSessionRegistry {
    let registry = this.stageRegistries.get(key);
    if (!registry) {
      registry = new InMemoryAgentSessionRegistry();
      this.stageRegistries.set(key, registry);
    }
    return registry;
  }

  private async closeAllStageRegistries(): Promise<void> {
    const registries = [...this.stageRegistries.values()];
    this.stageRegistries.clear();
    await Promise.allSettled(registries.map(r => r.closeAll()));
  }

  private async closeStageRegistry(key: string): Promise<void> {
    const registry = this.stageRegistries.get(key);
    if (!registry) return;
    this.stageRegistries.delete(key);
    await registry.closeAll();
  }

  private async closeStageRegistryForRun(
    workflowRun: StageAttemptRunInfo | undefined,
    stage: WorkflowStageId,
  ): Promise<void> {
    if (!workflowRun) return;
    await this.closeStageRegistry(this.stageAttemptKeyFromRun(workflowRun, stage));
  }

  private workKey(work: WorkflowWork): string {
    if (work.kind === 'task') return `task:${work.stage}:${work.taskId}`;
    if (work.kind === 'check') return `check:${work.stage}:${work.checkName}`;
    if (work.kind === 'await-approval') return `await-approval:${work.stage}`;
    if (work.kind === 'failed') return `failed:${work.reason.stage}:${work.reason.taskId ?? work.reason.checkName ?? work.reason.reason}`;
    if (work.kind === 'blocked') return `blocked:${work.stage}:${this.formatBlocked(work.stage, work.reason)}`;
    return work.kind;
  }

  private getRejectedApprovalOutput(issueId: string, stage: WorkflowStageId): unknown {
    const run = this.workflowRunService?.getLatestRunForIssue(issueId);
    const stageRun = run?.stageRuns.find(candidate => candidate.stage === stage);
    return stageRun?.approvalStatus === 'rejected' ? stageRun.approvalOutput : undefined;
  }

  private async materializeCurrentStageWork(
    issue: Issue,
    acpOptions: AgentSessionOptions,
    run: ReturnType<WorkflowApplicationRuntime['resumeDecision']>['run'],
  ): Promise<boolean> {
    const runner = this.getRunner(run.currentStage);
    if (!runner?.materializeWork) return false;

    const currentIssue = { ...this.refreshIssue(issue), stage: run.currentStage };
    const ctx = this.buildContext(currentIssue, acpOptions);
    return await runner.materializeWork(ctx);
  }

  private async resumeAfterMaterializingWork(
    issue: Issue,
    acpOptions: AgentSessionOptions,
  ): Promise<ReturnType<WorkflowApplicationRuntime['resumeDecision']>> {
    const service = this.workflowApplicationService;
    if (!service) throw new Error('WorkflowApplicationService is required for aggregate workflow execution');

    let decision = service.resumeDecision(issue.id);

    if (this.shouldMaterializeBeforeWork(decision.nextWork) && await this.materializeCurrentStageWork(issue, acpOptions, decision.run)) {
      decision = service.resumeDecision(issue.id);
    }

    return decision;
  }

  private async runAggregateWorkflow(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    const service = this.workflowApplicationService;
    if (!service) throw new Error('WorkflowApplicationService is required for aggregate workflow execution');

    let currentIssue = issue;
    let initial: ReturnType<WorkflowApplicationRuntime['startWorkflow']> | ReturnType<WorkflowApplicationRuntime['resumeDecision']>;
    if (issue.stage === Stage.Backlog) {
      initial = service.startWorkflow({ issueId: issue.id, issueNumber: issue.number });
    } else {
      const retryableFailedStage = this.workflowRunService?.canRetryStage?.(issue.id, issue.stage) ?? false;
      if (retryableFailedStage) {
        this.pendingRejectionFeedback.set(`${issue.id}:${issue.stage}`, this.getRejectedApprovalOutput(issue.id, issue.stage));
      }
      initial = retryableFailedStage
        ? service.retryStage({ issueId: issue.id, stage: issue.stage, startedBy: 'retry' })
        : service.resumeDecision(issue.id);
    }
    let run = initial.run;
    let work = 'decision' in initial ? initial.decision.nextWork : initial.nextWork;
    if (this.shouldMaterializeBeforeWork(work) && await this.materializeCurrentStageWork(issue, acpOptions, run)) {
      const materializedDecision = service.resumeDecision(issue.id);
      run = materializedDecision.run;
      work = materializedDecision.nextWork;
    }

    while (true) {
      if (this.signal?.aborted) {
        await this.closeStageRegistryForRun(run.snapshot(), run.currentStage);
        return { completed: false, stage: run.currentStage, message: 'Agent stopped by user' };
      }

      if (work.kind === 'complete') {
        await this.closeAllStageRegistries();
        this.checkpointManager.deleteAll(currentIssue.number);
        return { completed: true, stage: run.currentStage, message: 'Pipeline completed' };
      }

      if (work.kind === 'failed') {
        await this.closeStageRegistryForRun(run.snapshot(), work.reason.stage);
        return { completed: false, stage: work.reason.stage, message: this.formatFailure(work) };
      }

      if (work.kind === 'await-approval') {
        await this.closeStageRegistryForRun(run.snapshot(), work.stage);
        return { completed: false, stage: work.stage, message: `Awaiting ${work.stage} approval` };
      }

      if (work.kind === 'blocked') {
        await this.closeStageRegistryForRun(run.snapshot(), work.stage);
        return { completed: false, stage: work.stage, message: this.formatBlocked(work.stage, work.reason) };
      }

      const runner = this.getRunner(work.stage);
      if (!runner) {
        return { completed: false, stage: work.stage, message: `Pipeline cannot handle stage: ${work.stage}` };
      }

      currentIssue = this.refreshIssue({ ...currentIssue, stage: work.stage });
      const ctx = this.buildContext(currentIssue, acpOptions, work);
      const beforeWorkKey = this.workKey(work);
      const beforeSnapshot = JSON.stringify(run.snapshot());
      const result = await runner.run(ctx);

      const latestRun = this.workflowRunService?.getLatestRunForIssue(issue.id);
      if (latestRun?.status === 'passed') {
        await this.closeAllStageRegistries();
        this.checkpointManager.deleteAll(currentIssue.number);
        return { completed: true, stage: latestRun.currentStage, message: 'Pipeline completed' };
      }
      if (latestRun?.status === 'failed') {
        const failedStage = latestRun.stageRuns.find(stageRun => stageRun.status === 'failed');
        const failedTask = failedStage?.tasks.find(task => task.status === 'failed');
        const failedCheck = failedStage?.checks.find(check => check.status === 'failed' || check.status === 'error');
        if (failedStage) {
          await this.closeStageRegistryForRun(latestRun, failedStage.stage);
        } else {
          await this.closeStageRegistryForRun(ctx.workflowRun, ctx.issue.stage);
        }
        return {
          completed: false,
          stage: failedStage?.stage ?? ctx.issue.stage,
          message: failedTask?.reason ?? failedCheck?.message ?? failedTask?.taskId ?? failedCheck?.checkName ?? result.message,
        };
      }

      const decision = await this.resumeAfterMaterializingWork(issue, acpOptions);
      run = decision.run;
      work = decision.nextWork;
      const nextStage = 'stage' in work ? work.stage : work.kind === 'failed' ? work.reason.stage : run.currentStage;
      const latestStageAttemptRun = this.workflowRunService?.getActiveRunForIssue(issue.id)
        ?? this.workflowRunService?.getLatestRunForIssue(issue.id)
        ?? run.snapshot();
      const nextStageAttemptKey = this.stageAttemptKeyFromRun(latestStageAttemptRun, ctx.issue.stage);
      const previousStageAttemptKey = ctx.workflowRun ? this.stageAttemptKeyFromRun(ctx.workflowRun, ctx.issue.stage) : null;
      if (nextStage !== ctx.issue.stage || work.kind === 'complete' || (previousStageAttemptKey !== null && nextStageAttemptKey !== previousStageAttemptKey)) {
        await this.closeStageRegistryForRun(ctx.workflowRun, ctx.issue.stage);
      }

      if (this.workKey(work) === beforeWorkKey && JSON.stringify(run.snapshot()) === beforeSnapshot) {
        const noProgressMessage = `Aggregate workflow made no progress while executing ${beforeWorkKey}`;
        return {
          completed: false,
          stage: ctx.issue.stage,
          message: result.success === false && result.message
            ? `${result.message}; ${noProgressMessage}`
            : noProgressMessage,
        };
      }

      if (!result.success && work.kind !== 'task' && work.kind !== 'check' && work.kind !== 'await-approval' && work.kind !== 'failed') {
        return { completed: false, stage: ctx.issue.stage, message: result.message };
      }
    }
  }

  private shouldMaterializeBeforeWork(work: WorkflowWork): boolean {
    if (work.kind === 'task' || work.kind === 'check') return true;
    return work.kind === 'blocked'
      && !work.reason.complete
      && work.reason.reason === 'dynamic-source-not-evaluated';
  }

  async run(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    try {
      return await this.runInner(issue, acpOptions);
    } finally {
      await this.closeAllStageRegistries();
    }
  }

  private async runInner(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    if (this.signal?.aborted) {
      return { completed: false, stage: issue.stage, message: 'Agent stopped by user' };
    }

    if (!this.workflowApplicationService) {
      return {
        completed: false,
        stage: issue.stage,
        message: 'WorkflowApplicationService is required for workflow execution',
      };
    }

    return this.runAggregateWorkflow(issue, acpOptions);
  }
}
