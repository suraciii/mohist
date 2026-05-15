import { Stage, type Issue } from '../types';
import type { StageRunner } from './check-stage-runner';
import type { StageContext, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, WorkflowApplicationRuntime, StageRunResult } from './stage-context';
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
import type { TaskRunSnapshot, WorkflowWork } from './domain';

export interface PipelineResult {
  completed: boolean;
  stage: Stage;
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
    };
  }

  private getTasksPath(issue: Issue): string | undefined {
    const changeDir = this.artifactManager.getChangeDir(issue.number);
    return changeDir ? `${changeDir}/tasks.json` : undefined;
  }

  private refreshIssue(issue: Issue): Issue {
    return this.issueRepo.findById(issue.id) ?? issue;
  }

  private getRunner(stage: Stage): StageRunner | null {
    return this.runners.find(r => r.canHandle(stage)) ?? null;
  }

  private formatFailure(work: Extract<WorkflowWork, { kind: 'failed' }>): string {
    const reason = work.reason;
    const subject = reason.taskId ?? reason.checkName ?? reason.stage;
    return reason.message ?? `${reason.reason}: ${subject}`;
  }

  private workKey(work: WorkflowWork): string {
    if (work.kind === 'task') return `task:${work.stage}:${work.taskId}`;
    if (work.kind === 'check') return `check:${work.stage}:${work.checkName}`;
    if (work.kind === 'await-approval') return `await-approval:${work.stage}`;
    if (work.kind === 'failed') return `failed:${work.reason.stage}:${work.reason.taskId ?? work.reason.checkName ?? work.reason.reason}`;
    return work.kind;
  }

  private getRejectedApprovalOutput(issueId: string, stage: Stage): unknown {
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
    tasksPath?: string,
  ): Promise<ReturnType<WorkflowApplicationRuntime['resumeDecision']>> {
    const service = this.workflowApplicationService;
    if (!service) throw new Error('WorkflowApplicationService is required for aggregate workflow execution');

    let decision = service.resumeDecision(issue.id, { tasksPath });

    if (this.shouldMaterializeBeforeWork(decision.nextWork) && await this.materializeCurrentStageWork(issue, acpOptions, decision.run)) {
      decision = service.resumeDecision(issue.id, { tasksPath });
    }

    return decision;
  }

  private async runAggregateWorkflow(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    const service = this.workflowApplicationService;
    if (!service) throw new Error('WorkflowApplicationService is required for aggregate workflow execution');

    let currentIssue = issue;
    const tasksPath = this.getTasksPath(issue);
    let initial: ReturnType<WorkflowApplicationRuntime['startWorkflow']> | ReturnType<WorkflowApplicationRuntime['resumeDecision']>;
    if (issue.stage === Stage.Backlog) {
      initial = service.startWorkflow({ issueId: issue.id, issueNumber: issue.number, tasksPath });
    } else {
      const retryableFailedStage = this.workflowRunService?.canRetryStage?.(issue.id, issue.stage) ?? false;
      if (retryableFailedStage) {
        this.pendingRejectionFeedback.set(`${issue.id}:${issue.stage}`, this.getRejectedApprovalOutput(issue.id, issue.stage));
      }
      initial = retryableFailedStage
        ? service.retryStage({ issueId: issue.id, stage: issue.stage, tasksPath, startedBy: 'retry' })
        : service.resumeDecision(issue.id, { tasksPath });
    }
    let run = initial.run;
    let work = 'decision' in initial ? initial.decision.nextWork : initial.nextWork;
    if (this.shouldMaterializeBeforeWork(work) && await this.materializeCurrentStageWork(issue, acpOptions, run)) {
      const materializedDecision = service.resumeDecision(issue.id, { tasksPath });
      run = materializedDecision.run;
      work = materializedDecision.nextWork;
    }

    while (true) {
      if (this.signal?.aborted) {
        return { completed: false, stage: run.currentStage, message: 'Agent stopped by user' };
      }

      if (work.kind === 'complete') {
        this.checkpointManager.deleteAll(currentIssue.number);
        return { completed: true, stage: Stage.Done, message: 'Pipeline completed' };
      }

      if (work.kind === 'failed') {
        return { completed: false, stage: work.reason.stage, message: this.formatFailure(work) };
      }

      if (work.kind === 'await-approval') {
        return { completed: false, stage: work.stage, message: `Awaiting ${work.stage} approval` };
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
        this.checkpointManager.deleteAll(currentIssue.number);
        return { completed: true, stage: Stage.Done, message: 'Pipeline completed' };
      }
      if (latestRun?.status === 'failed') {
        const failedStage = latestRun.stageRuns.find(stageRun => stageRun.status === 'failed');
        const failedTask = failedStage?.tasks.find(task => task.status === 'failed');
        const failedCheck = failedStage?.checks.find(check => check.status === 'failed' || check.status === 'error');
        return {
          completed: false,
          stage: failedStage?.stage ?? ctx.issue.stage,
          message: failedTask?.reason ?? failedCheck?.message ?? failedTask?.taskId ?? failedCheck?.checkName ?? result.message,
        };
      }

      const decision = await this.resumeAfterMaterializingWork(issue, acpOptions, tasksPath);
      run = decision.run;
      work = decision.nextWork;

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
    return work.kind === 'task' || work.kind === 'check';
  }

  async run(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    if (this.signal?.aborted) {
      return { completed: false, stage: issue.stage, message: 'Agent stopped by user' };
    }

    if (this.workflowApplicationService) {
      return this.runAggregateWorkflow(issue, acpOptions);
    }

    let currentIssue = issue;
    const stageVisitCounts = new Map<Stage, number>();
    const MAX_STAGE_VISITS = 5;

    while (currentIssue.stage !== Stage.Done) {
      if (this.signal?.aborted) {
        return { completed: false, stage: currentIssue.stage, message: 'Agent stopped by user' };
      }

      const visitCount = (stageVisitCounts.get(currentIssue.stage) ?? 0) + 1;
      stageVisitCounts.set(currentIssue.stage, visitCount);

      if (visitCount > MAX_STAGE_VISITS) {
        return {
          completed: false,
          stage: currentIssue.stage,
          message: `Stage ${currentIssue.stage} reached max visit limit (${MAX_STAGE_VISITS}) — possible escalation loop`,
        };
      }

      const ctx = this.buildContext(currentIssue, acpOptions);
      const runners = this.runners.filter(r => r.canHandle(currentIssue.stage));
      if (runners.length === 0) {
        return { completed: false, stage: currentIssue.stage, message: `Pipeline cannot handle stage: ${currentIssue.stage}` };
      }

      let result: StageRunResult | null = null;
      for (const runner of runners) {
        const stageResult = await runner.run(ctx);
        if (stageResult.message === 'ConfigDrivenStageRunner requires WorkflowRun requestedWork') {
          continue;
        }
        result = stageResult;
        break;
      }
      if (!result) {
        return { completed: false, stage: currentIssue.stage, message: `Pipeline cannot handle stage without aggregate workflow service: ${currentIssue.stage}` };
      }

      if (result.success) {
        return { completed: false, stage: currentIssue.stage, message: 'Stage completed but aggregate workflow service is unavailable' };
      } else {
        return { completed: false, stage: currentIssue.stage, message: result.message };
      }
    }
    this.checkpointManager.deleteAll(currentIssue.number);

    return { completed: true, stage: Stage.Done, message: 'Pipeline completed' };
  }
}
