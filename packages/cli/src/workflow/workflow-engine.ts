import { Stage, type Issue } from '../types';
import type { StageRunner } from './check-stage-runner';
import type { StageContext, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo, WorkflowApplicationRuntime } from './stage-context';
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
    const resolvedModel = this.config ? resolveStageModel(stage, this.config, issue) : undefined;
    const wfObservers = createWorkflowSessionObservers({
      eventBus: this.eventBus,
      workflowLogRepo: this.workflowLogRepo,
      sessionStreamLogRepo: this.sessionStreamLogRepo,
      coderSessionRepo: this.coderSessionRepo,
    });
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
      signal: this.signal,
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

  private async runAggregateWorkflow(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    const service = this.workflowApplicationService;
    if (!service) throw new Error('WorkflowApplicationService is required for aggregate workflow execution');

    let currentIssue = issue;
    const tasksPath = this.getTasksPath(issue);
    const initial = issue.stage === Stage.Backlog
      ? service.startWorkflow({ issueId: issue.id, issueNumber: issue.number, tasksPath })
      : service.resumeDecision(issue.id, { tasksPath });
    let run = initial.run;
    let work = 'decision' in initial ? initial.decision.nextWork : initial.nextWork;

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

      const decision = service.resumeDecision(issue.id, { tasksPath });
      run = decision.run;
      work = decision.nextWork;

      if (this.workKey(work) === beforeWorkKey && JSON.stringify(run.snapshot()) === beforeSnapshot) {
        return {
          completed: false,
          stage: ctx.issue.stage,
          message: `Aggregate workflow made no progress while executing ${beforeWorkKey}`,
        };
      }

      if (!result.success && work.kind !== 'task' && work.kind !== 'check' && work.kind !== 'await-approval' && work.kind !== 'failed') {
        return { completed: false, stage: ctx.issue.stage, message: result.message };
      }
    }
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

      const runner = this.runners.find(r => r.canHandle(currentIssue.stage));
      if (!runner) {
        return { completed: false, stage: currentIssue.stage, message: `Pipeline cannot handle stage: ${currentIssue.stage}` };
      }

      const ctx = this.buildContext(currentIssue, acpOptions);
      const result = await runner.run(ctx);

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
