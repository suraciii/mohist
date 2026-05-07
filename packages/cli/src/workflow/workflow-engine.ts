import { Stage, IssueStatus, type Issue } from '../types';
import type { StageRunner } from './check-stage-runner';
import type { StageContext, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo } from './stage-context';
import type { CheckpointManager } from './checkpoint-manager';
import type { EventBus } from '../services/event-bus';
import type { AgentSessionOptions } from '../agent-runtime/agent-session';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { StageExecutionRepo } from '../db/stage-execution-repo';
import type { ConfigInfo } from '../config/config-schema';
import { resolveStageModel } from '../config/model-resolution';

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
    this.config = options.config;
  }

  private buildContext(issue: Issue, acpOptions: AgentSessionOptions): StageContext {
    const resolvedModel = this.config ? resolveStageModel(issue.stage, this.config) : undefined;
    return {
      issue,
      acpOptions: {
        ...acpOptions,
        signal: this.signal,
        ...(resolvedModel !== undefined ? { model: resolvedModel } : {}),
        ...(this.coderSessionRepo ? { coderSessionRepo: this.coderSessionRepo } : {}),
        ...(this.workflowLogRepo ? { workflowLogRepo: this.workflowLogRepo } : {}),
        ...(this.sessionStreamLogRepo ? { sessionStreamLogRepo: this.sessionStreamLogRepo } : {}),
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
      signal: this.signal,
    };
  }

  async run(issue: Issue, acpOptions: AgentSessionOptions): Promise<PipelineResult> {
    if (this.signal?.aborted) {
      return { completed: false, stage: issue.stage, message: 'Agent stopped by user' };
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

      if (currentIssue.stage === Stage.Backlog || currentIssue.stage === Stage.Draft) {
        const updated = this.issueRepo.updateStage(currentIssue.id, Stage.Plan);
        if (updated) {
          currentIssue = updated;
        }
      }

      const ctx = this.buildContext(currentIssue, acpOptions);
      const result = await runner.run(ctx);

      if (result.success) {
        if (result.nextStage !== undefined) {
          if (result.nextStage === Stage.Done && currentIssue.stage === Stage.Check) {
            return { completed: false, stage: currentIssue.stage, message: 'Check stage cannot transition directly to Done; must wait for merge completion' };
          }
          const updated = this.issueRepo.updateStage(currentIssue.id, result.nextStage);
          if (updated) {
            currentIssue = updated;
          } else {
            return { completed: false, stage: currentIssue.stage, message: `Failed to update stage to ${result.nextStage}` };
          }
        } else {
          return { completed: false, stage: currentIssue.stage, message: 'Stage completed but no next stage specified' };
        }
      } else if (result.escalateToStage !== undefined) {
        if (result.escalateToStage === Stage.Done && currentIssue.stage === Stage.Check) {
          return { completed: false, stage: currentIssue.stage, message: 'Check stage cannot escalate to Done; must wait for merge completion' };
        }
        const updated = this.issueRepo.updateStage(currentIssue.id, result.escalateToStage);
        if (updated) {
          currentIssue = updated;
        } else {
          return { completed: false, stage: currentIssue.stage, message: `Failed to escalate to stage ${result.escalateToStage}` };
        }
      } else {
        return { completed: false, stage: currentIssue.stage, message: result.message };
      }
    }

    this.issueRepo.clearApprovalState(currentIssue.id);
    this.issueRepo.updateStatus(currentIssue.id, IssueStatus.Completed);
    this.checkpointManager.deleteAll(currentIssue.number);

    return { completed: true, stage: Stage.Done, message: 'Pipeline completed' };
  }
}
