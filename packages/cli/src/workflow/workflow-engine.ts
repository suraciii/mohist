import { Stage, IssueStatus, type Issue } from '../types';
import type { StageRunner } from './check-stage-runner';
import type { StageContext, IssueRepo, ChangeArtifactsManager, WorktreeManager, ProjectRepo } from './stage-context';
import type { CheckpointManager } from './checkpoint-manager';
import type { EventBus, EventMap } from '../services/event-bus';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';

export interface PipelineResult {
  completed: boolean;
  stage: Stage;
  gateRequired: boolean;
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
  coderSessionRepo?: CoderSessionRepo;
}

export class WorkflowEngine {
  private runners: StageRunner[];
  private issueRepo: IssueRepo;
  private eventBus: EventBus;
  private checkpointManager: CheckpointManager;
  private artifactManager: ChangeArtifactsManager;
  private worktreeManager?: WorktreeManager;
  private projectRepo?: ProjectRepo;
  private projectId?: string;
  private signal?: AbortSignal;
  private workflowLogRepo?: WorkflowLogRepo;
  private coderSessionRepo?: CoderSessionRepo;

  constructor(options: WorkflowEngineOptions) {
    this.runners = options.runners;
    this.issueRepo = options.issueRepo;
    this.eventBus = options.eventBus;
    this.checkpointManager = options.checkpointManager;
    this.artifactManager = options.artifactManager;
    this.worktreeManager = options.worktreeManager;
    this.projectRepo = options.projectRepo;
    this.projectId = options.projectId;
    this.signal = options.signal;
    this.workflowLogRepo = options.workflowLogRepo;
    this.coderSessionRepo = options.coderSessionRepo;
  }

  private buildContext(issue: Issue, acpOptions: AcpConnectionOptions): StageContext {
    return {
      issue,
      acpOptions: { ...acpOptions, signal: this.signal },
      artifactManager: this.artifactManager,
      worktreeManager: this.worktreeManager as WorktreeManager,
      projectRepo: this.projectRepo as ProjectRepo,
      eventBus: this.eventBus,
      checkpointManager: this.checkpointManager,
      issueRepo: this.issueRepo,
      workflowLogRepo: this.workflowLogRepo,
      coderSessionRepo: this.coderSessionRepo,
      signal: this.signal,
    };
  }

  async run(issue: Issue, acpOptions: AcpConnectionOptions): Promise<PipelineResult> {
    if (this.signal?.aborted) {
      return { completed: false, stage: issue.stage, gateRequired: false, message: 'Agent stopped by user' };
    }

    let currentIssue = issue;

    while (currentIssue.stage !== Stage.Done) {
      if (this.signal?.aborted) {
        return { completed: false, stage: currentIssue.stage, gateRequired: false, message: 'Agent stopped by user' };
      }

      const runner = this.runners.find(r => r.canHandle(currentIssue.stage));
      if (!runner) {
        return { completed: false, stage: currentIssue.stage, gateRequired: false, message: `Pipeline cannot handle stage: ${currentIssue.stage}` };
      }

      const ctx = this.buildContext(currentIssue, acpOptions);
      const result = await runner.run(ctx);

      if (!result.success) {
        if (result.escalateToStage !== undefined) {
          return { completed: false, stage: result.escalateToStage, gateRequired: false, message: result.message ?? 'Stage failed, escalating' };
        }
        if (result.nextStage !== undefined) {
          const updated = this.issueRepo.updateStage(currentIssue.id, result.nextStage);
          if (updated) {
            currentIssue = updated;
            continue;
          }
        }
        return { completed: false, stage: currentIssue.stage, gateRequired: false, message: result.message };
      }

      if (result.requiresApproval) {
        this.issueRepo.updateStage(currentIssue.id, currentIssue.stage);
        this.issueRepo.setApprovalState(currentIssue.id, {
          stage: currentIssue.stage,
          status: 'awaiting',
          output: result.output,
          requestedAt: new Date().toISOString(),
        });
        this.emitSafe('approval_requested', {
          issueId: currentIssue.id,
          projectId: this.projectId ?? currentIssue.projectId,
          stage: currentIssue.stage,
        });
        return { completed: false, stage: currentIssue.stage, gateRequired: true, message: result.message ?? 'Stage completed, awaiting approval' };
      }

      const nextStage = result.nextStage;
      if (nextStage !== undefined) {
        const updated = this.issueRepo.updateStage(currentIssue.id, nextStage);
        if (updated) {
          currentIssue = updated;
        } else {
          return { completed: false, stage: currentIssue.stage, gateRequired: false, message: `Failed to update stage to ${nextStage}` };
        }
      } else {
        return { completed: false, stage: currentIssue.stage, gateRequired: false, message: 'Stage completed but no next stage specified' };
      }
    }

    this.issueRepo.updateStage(currentIssue.id, Stage.Done);
    this.issueRepo.clearApprovalState(currentIssue.id);
    this.issueRepo.updateStatus(currentIssue.id, IssueStatus.Completed);
    this.checkpointManager.deleteAll(currentIssue.number);

    return { completed: true, stage: Stage.Done, gateRequired: false, message: 'Pipeline completed' };
  }

  private emitSafe<T extends keyof EventMap>(event: T, data: EventMap[T]): void {
    try {
      this.eventBus.emit(event, data);
    } catch {
      // swallow emit errors
    }
  }
}
