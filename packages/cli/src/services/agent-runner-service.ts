import type { IssueRepo } from '../db/issue-repo';
import type { ProjectRepo } from '../db/project-repo';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { CommentRepo } from '../db/comment-repo';
import type { LlmConfig } from '../agent-runtime';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import type { ChildProcess } from 'child_process';
import { WorkflowController, type PipelineResult } from '../workflow/workflow-controller';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { IssueStatus, MergeState, type Issue } from '../types';
import { EventBus } from './event-bus';
import { Stage } from '../types';
import { load } from '../config/config-loader';
import { maskSensitiveData } from '../utils/sensitive-data';
import { Log } from '../util/log';
import { PipelineCheckpointRepo } from '../db/pipeline-checkpoint-repo';
import { findChangeDir } from '../openspec/detector';
import { WorktreeManager } from '../git/worktree-manager';
import * as fs from 'fs';
import * as path from 'path';

export interface AgentProgress {
  stage: string;
  roundType?: string;
  roundIndex?: number;
  taskProgress?: { completed: number; total: number } | null;
  lastActivityAt: string;
}

export interface RunningAgent {
  issueId: string;
  issueNumber: number;
  promise: Promise<void>;
  projectId: string;
  progress: AgentProgress;
  childProcess?: ChildProcess;
}

export interface RecoverableIssue {
  issueNumber: number;
  stage: string;
}

export interface AgentStatus {
  running: boolean;
  issueId: string | null;
  issueNumber: number | null;
  activeAgents: Array<{ issueId: string; issueNumber: number; projectId: string; progress: AgentProgress }>;
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>;
  recoverableIssues: RecoverableIssue[];
  queueDepth: number;
  maxConcurrentAgents: number;
}

export interface WaitingQuestion {
  questionId: string;
  question: string;
}

const log = Log.create({ service: 'agent-runner' });

export interface PipelineGateInfo {
  issueId: string;
  issueNumber: number;
  projectId: string;
  stage: Stage;
}

export class AgentRunnerService {
  private activeAgents = new Map<string, RunningAgent>();
  private pendingGates = new Map<number, PipelineGateInfo>();
  private waitingQuestions = new Map<string, WaitingQuestion>();
  private readonly maxConcurrentAgents: number;
  private recoverableIssues: RecoverableIssue[];
  private llmConfig?: LlmConfig;
  private readonly providersChangedListener: (data: { providers: Array<{ id: string; name?: string; apiKey?: string; baseURL?: string; sdk?: string; models?: string[] }> }) => void;

  constructor(
    private readonly eventBus: EventBus,
    private readonly workflowLogRepo?: WorkflowLogRepo,
    private readonly issueRepo?: IssueRepo,
    maxConcurrentAgents: number = 8,
    _agentSessionMessageRepo?: unknown,
    private readonly coderSessionRepo?: CoderSessionRepo,
    private readonly checkpointRepo?: PipelineCheckpointRepo,
    private readonly projectRepo?: ProjectRepo,
    private readonly worktreeManager?: WorktreeManager,
    private readonly opencodeBinPath?: string,
    private readonly commentRepo?: CommentRepo,
  ) {
    this.maxConcurrentAgents = maxConcurrentAgents;
    this.recoverableIssues = this.detectRecoverableIssues();
    log.info('AgentRunnerService initialized', { maxConcurrentAgents: this.maxConcurrentAgents });
    if (this.recoverableIssues.length > 0) {
      log.info('Detected recoverable issues', { count: this.recoverableIssues.length, issues: this.recoverableIssues.map(i => `#${i.issueNumber} (${i.stage})`).join(', ') });
    }

    this.providersChangedListener = (_data) => {
      this.handleProvidersChanged();
    };
    this.eventBus.on('config:providers:changed', this.providersChangedListener);
  }

  shutdown(): void {
    this.eventBus.off('config:providers:changed', this.providersChangedListener);
  }

  private handleProvidersChanged(): void {
    try {
      log.info('Provider config changed, reloading LLM config');
      const freshConfig = load();
      this.llmConfig = freshConfig;
      const maskedConfig = maskSensitiveData(freshConfig as unknown as Record<string, unknown>);
      log.info('LLM config reloaded successfully', { config: JSON.stringify(maskedConfig) });
    } catch (err) {
      log.error('Failed to reload LLM config', { error: err instanceof Error ? err.message : String(err) });
    }
  }

  setLlmConfig(config: LlmConfig): void {
    this.llmConfig = config;
  }

  getLlmConfig(): LlmConfig | undefined {
    return this.llmConfig;
  }

  private detectRecoverableIssues(): RecoverableIssue[] {
    if (!this.issueRepo) return [];
    const activeIssues = this.issueRepo.findAll({ status: IssueStatus.Active });
    return activeIssues
      .filter(issue => issue.stage !== Stage.Backlog)
      .map(issue => ({ issueNumber: issue.number, stage: issue.stage }));
  }

  recoverIssues(): void {
    if (!this.issueRepo) return;

    const orphans = this.issueRepo.findAll({ status: IssueStatus.Active })
      .filter(issue => issue.stage !== Stage.Backlog);

    if (orphans.length === 0) return;

    for (const issue of orphans) {
      try {
        if (issue.approvalState?.status === 'awaiting') {
          this.pendingGates.set(issue.number, {
            issueId: issue.id,
            issueNumber: issue.number,
            projectId: issue.projectId,
            stage: issue.approvalState.stage ?? issue.stage,
          });
          log.info('Restored pending gate for awaiting issue', {
            issueNumber: issue.number,
            stage: issue.approvalState.stage ?? issue.stage,
            action: 'pendingGate restored, status remains active',
          });
        } else if (issue.stage === Stage.Build && this.projectRepo && this.worktreeManager) {
          this.recoverBuildStageIssue(issue);
        } else {
          this.issueRepo.updateStatus(issue.id, IssueStatus.Interrupted);
          log.info('Recovered orphaned issue', {
            issueNumber: issue.number,
            stage: issue.stage,
            action: 'status=interrupted, stage preserved, checkpoint preserved',
          });
        }
      } catch (err) {
        log.error('Failed to recover orphaned issue', {
          issueNumber: issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    this.recoverableIssues = [];
  }

  private recoverBuildStageIssue(issue: Issue): void {
    const project = this.projectRepo!.findById(issue.projectId);
    if (!project) {
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — project not found', {
        issueNumber: issue.number,
        action: 'status=blocked, project lookup failed',
      });
      return;
    }

    const worktreePath = this.worktreeManager!.getPath(project.name, issue.number);
    if (!worktreePath) {
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — no worktree found', {
        issueNumber: issue.number,
        action: 'status=blocked, worktree not found',
      });
      return;
    }

    const changeDir = findChangeDir(worktreePath, issue.number);
    if (!changeDir) {
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — missing tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, no change directory found',
      });
      return;
    }

    const tasksPath = path.join(changeDir, 'tasks.json');
    if (!fs.existsSync(tasksPath)) {
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — missing tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, change directory exists but no tasks.json',
      });
      return;
    }

    let tasksFile: { version: number; tasks: Array<{ id: string; passes: boolean }> };
    try {
      const raw = fs.readFileSync(tasksPath, 'utf-8');
      tasksFile = JSON.parse(raw);
    } catch {
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — malformed tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, tasks.json parse failed',
      });
      return;
    }

    if (!tasksFile.tasks || !Array.isArray(tasksFile.tasks)) {
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — malformed tasks.json', {
        issueNumber: issue.number,
        action: 'status=blocked, tasks.json missing tasks array',
      });
      return;
    }

    const allPass = tasksFile.tasks.every(t => t.passes === true);
    if (allPass) {
      this.issueRepo!.updateStage(issue.id, Stage.Review);
      const updatedIssue = this.issueRepo!.findById(issue.id);
      if (!updatedIssue) {
        this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
        this.issueRepo!.clearApprovalState(issue.id);
        log.info('Recovered build-stage orphan — failed to re-fetch after stage update', {
          issueNumber: issue.number,
          action: 'status=blocked, re-fetch returned null',
        });
        return;
      }

      const acpOptions: AcpConnectionOptions = {
        cwd: worktreePath,
        issueId: updatedIssue.id,
        projectId: issue.projectId,
        workflowLogRepo: this.workflowLogRepo,
        coderSessionRepo: this.coderSessionRepo,
        eventBus: this.eventBus,
        issueNumber: updatedIssue.number,
        opencodeBinPath: this.opencodeBinPath,
        stage: Stage.Review,
      };

      const startResult = this.startPipeline(
        updatedIssue,
        issue.projectId,
        this.issueRepo!,
        worktreePath,
        acpOptions,
      );

      if (startResult.started) {
        log.info('Recovered build-stage orphan — all tasks pass, review pipeline started', {
          issueNumber: issue.number,
          totalTasks: tasksFile.tasks.length,
          action: 'stage=review, pipeline started',
        });
      } else {
        this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
        this.issueRepo!.clearApprovalState(issue.id);
        log.info('Recovered build-stage orphan — all tasks pass but pipeline start failed', {
          issueNumber: issue.number,
          totalTasks: tasksFile.tasks.length,
          error: startResult.error,
          action: 'status=blocked, pipeline could not start',
        });
      }
    } else {
      const passed = tasksFile.tasks.filter(t => t.passes === true).length;
      const pending = tasksFile.tasks.filter(t => t.passes !== true);
      const pendingIds = pending.map(t => t.id).join(', ');
      this.issueRepo!.updateStatus(issue.id, IssueStatus.Blocked);
      this.issueRepo!.clearApprovalState(issue.id);
      log.info('Recovered build-stage orphan — partial progress', {
        issueNumber: issue.number,
        action: `status=blocked, ${passed}/${tasksFile.tasks.length} tasks completed, ${pendingIds} pending`,
      });
    }
  }

  getMaxConcurrentAgents(): number {
    return this.maxConcurrentAgents;
  }

  setWaiting(issueId: string, questionId: string, question: string): void {
    this.waitingQuestions.set(issueId, { questionId, question });
  }

  clearWaiting(issueId: string): void {
    this.waitingQuestions.delete(issueId);
  }

  getWaitingQuestions(): Map<string, WaitingQuestion> {
    return this.waitingQuestions;
  }

  isRunning(issueId?: string): boolean {
    if (issueId !== undefined) {
      return this.activeAgents.has(issueId);
    }
    return this.activeAgents.size > 0;
  }

  isRunningByNumber(issueNumber: number): boolean {
    for (const agent of this.activeAgents.values()) {
      if (agent.issueNumber === issueNumber) return true;
    }
    return false;
  }

  getStatus(): AgentStatus {
    const agents = Array.from(this.activeAgents.values()).map((a) => ({
      issueId: a.issueId,
      issueNumber: a.issueNumber,
      projectId: a.projectId,
      progress: { ...a.progress },
    }));

    const waiting = Array.from(this.waitingQuestions.entries()).map(([issueId, wq]) => {
      const agent = this.activeAgents.get(issueId);
      return {
        issueId,
        issueNumber: agent?.issueNumber ?? 0,
        projectId: agent?.projectId ?? '',
        questionId: wq.questionId,
        question: wq.question,
      };
    });

    const first = agents[0];

    return {
      running: this.activeAgents.size > 0,
      issueId: first != null ? first.issueId : null,
      issueNumber: first != null ? first.issueNumber : null,
      activeAgents: agents,
      waitingQuestions: waiting,
      recoverableIssues: this.recoverableIssues,
      queueDepth: 0,
      maxConcurrentAgents: this.maxConcurrentAgents,
    };
  }

  forceStop(issueId: string): { stopped: boolean; issueNumber?: number } {
    const agent = this.activeAgents.get(issueId);
    if (!agent) {
      return { stopped: false };
    }

    if (agent.childProcess) {
      try {
        agent.childProcess.kill('SIGKILL');
        log.info('Force-stopped agent child process', { issueNumber: agent.issueNumber, issueId: issueId.slice(0, 8) });
      } catch (err) {
        log.warn('Failed to kill child process during force stop', {
          issueNumber: agent.issueNumber,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    this.activeAgents.delete(issueId);

    for (const [num, gate] of this.pendingGates.entries()) {
      if (gate.issueId === issueId) {
        this.pendingGates.delete(num);
        break;
      }
    }

    this.waitingQuestions.delete(issueId);

    this.eventBus.emit('agent_stopped', {
      issueId,
      projectId: agent.projectId,
      issueNumber: agent.issueNumber,
      reason: 'force_stop',
    });

    log.info('Agent force-stopped', { issueNumber: agent.issueNumber, issueId: issueId.slice(0, 8) });

    return { stopped: true, issueNumber: agent.issueNumber };
  }

  getActiveIssueId(): string | null {
    if (this.activeAgents.size === 0) return null;
    const first = this.activeAgents.values().next().value;
    return first != null ? first.issueId : null;
  }

  hasPendingGate(issueNumber: number): boolean {
    return this.pendingGates.has(issueNumber);
  }

  startPipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): { started: boolean; error?: string } {
    if (this.activeAgents.has(issue.id)) {
      return { started: false, error: `Issue #${issue.number} already has an agent running` };
    }

    if (this.activeAgents.size >= this.maxConcurrentAgents) {
      return { started: false, error: `Concurrent agent limit reached (${this.maxConcurrentAgents})` };
    }

    const pendingApproval = issueRepo.findPendingApprovalByIssueId(issue.id);
    if (pendingApproval) {
      return {
        started: false,
        error: `Issue #${issue.number} has pending approval.`,
      };
    }

    this.executePipeline(issue, projectId, issueRepo, worktreePath, acpOptions, updateIssueStatus);
    return { started: true };
  }

  resumePipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    if (this.activeAgents.has(issue.id)) {
      throw new Error(`Issue #${issue.number} is already running`);
    }

    this.pendingGates.delete(issue.number);
    this.executePipeline(issue, projectId, issueRepo, worktreePath, acpOptions, updateIssueStatus);
  }

  private executePipeline(
    issue: Issue,
    projectId: string,
    issueRepo: IssueRepo,
    worktreePath: string,
    acpOptions: AcpConnectionOptions,
    updateIssueStatus?: (issueId: string, status: IssueStatus) => void,
  ): void {
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    log.info('Pipeline started', { issueNumber: issue.number, projectId });

    const progress: AgentProgress = {
      stage: issue.stage,
      lastActivityAt: new Date().toISOString(),
    };

    const startTime = Date.now();
    let conflictResolutionInitiated = false;
    let deferredRestartWorktreePath: string | null = null;
    const promise = (async () => {
      try {
        const artifactManager = new ChangeArtifactsManager(worktreePath);

        const controllerOptions: import('../workflow/workflow-controller').WorkflowControllerOptions = {
          artifactManager,
          worktreePath,
          issueRepo,
          eventBus: this.eventBus,
          projectId,
          checkpointRepo: this.checkpointRepo,
          onProgress: (update) => {
            if (update.stage !== undefined) progress.stage = update.stage;
            if (update.roundType !== undefined) progress.roundType = update.roundType;
            if (update.roundIndex !== undefined) progress.roundIndex = update.roundIndex;
            if (update.taskProgress !== undefined) progress.taskProgress = update.taskProgress;
            progress.lastActivityAt = new Date().toISOString();
          },
          onChildProcess: (proc) => {
            const agent = this.activeAgents.get(issue.id);
            if (agent) agent.childProcess = proc;
          },
          commentRepo: this.commentRepo,
        };

        if (this.worktreeManager && this.projectRepo) {
          controllerOptions.mergeBackFn = async (issueNumber: number) => {
            const project = this.projectRepo!.findById(projectId);
            if (!project) {
              return { success: false, message: `Project not found: ${projectId}` };
            }
            return this.worktreeManager!.mergeBack(project.path, project.name, issueNumber, project.baseBranch);
          };

          controllerOptions.onMergeConflictFn = async (issueNumber: number) => {
            const project = this.projectRepo!.findById(projectId);
            if (!project) {
              log.error('onMergeConflict: project not found', { projectId });
              issueRepo.setMergeState(issue.id, MergeState.Blocked);
              return;
            }

            if (!this.worktreeManager!.exists(project.name, issueNumber)) {
              log.error('onMergeConflict: worktree not found', { issueNumber, projectName: project.name });
              issueRepo.setMergeState(issue.id, MergeState.Blocked);
              return;
            }

            const reverseResult = await this.worktreeManager!.rebaseOntoMaster(
              project.path, project.name, issueNumber, project.baseBranch,
            );

            if (!reverseResult.success && !reverseResult.conflicts.length) {
              log.error('onMergeConflict: rebase failed with no conflicts', {
                issueNumber,
              });
              issueRepo.setMergeState(issue.id, MergeState.Blocked);
              this.eventBus.emit('merge_blocked', {
                issueId: issue.id,
                projectId,
                issueNumber,
                conflictingFiles: [],
                retryCount: 0,
              });
              return;
            }

            const currentIssue = issueRepo.findById(issue.id);
            if (!currentIssue) {
              log.error('onMergeConflict: issue not found', { issueId: issue.id });
              return;
            }

            const currentRetryCount = (currentIssue.conflictRetryCount ?? 0) + 1;
            issueRepo.updateConflictRetryCount(issue.id, currentRetryCount);

            if (currentRetryCount >= 3) {
              issueRepo.setMergeState(issue.id, MergeState.Blocked);
              this.eventBus.emit('merge_blocked', {
                issueId: issue.id,
                projectId,
                issueNumber,
                conflictingFiles: reverseResult.conflicts,
                retryCount: currentRetryCount,
              });
              log.warn('onMergeConflict: max retries reached, blocked', {
                issueNumber,
                retryCount: currentRetryCount,
              });
              return;
            }

            issueRepo.update(issue.id, { stage: Stage.Build, status: IssueStatus.Active });
            issueRepo.setMergeState(issue.id, MergeState.Resolving);
            issueRepo.clearApprovalState(issue.id);

            const wtPath = this.worktreeManager!.getPath(project.name, issueNumber);
            if (!wtPath) {
              log.error('onMergeConflict: worktree path not found after rebase', { issueNumber });
              return;
            }

            const conflictFiles = reverseResult.conflicts;
            this.eventBus.emit('merge_blocked', {
              issueId: issue.id,
              projectId,
              issueNumber,
              conflictingFiles: conflictFiles,
              retryCount: currentRetryCount,
            });
            log.info('onMergeConflict: conflict resolution initiated', {
              issueNumber,
              conflictFiles,
              retryCount: currentRetryCount,
            });

            conflictResolutionInitiated = true;
            deferredRestartWorktreePath = wtPath;
          };
        }

        const pipeline = new WorkflowController(controllerOptions);

        const result: PipelineResult = await pipeline.run(issue, acpOptions);

        if (result.gateRequired) {
          this.pendingGates.set(issue.number, {
            issueId: issue.id,
            issueNumber: issue.number,
            projectId,
            stage: result.stage,
          });
          this.eventBus.emit('agent_paused', {
            issueId: issue.id,
            projectId,
            issueNumber: issue.number,
          });
          log.info('Pipeline paused at gate', {
            issueNumber: issue.number,
            stage: result.stage,
          });
        }

        const duration = Date.now() - startTime;
        log.info('Pipeline run completed', { issueNumber: issue.number, duration, completed: result.completed });
        if (result.completed) {
          this.eventBus.emit('agent_completed', { issueId: issue.id, projectId, issueNumber: issue.number });
        } else if (conflictResolutionInitiated) {
          log.info('Conflict resolution initiated, pipeline restart deferred', {
            issueNumber: issue.number,
          });
        } else if (!result.gateRequired) {
          try {
            issueRepo.setApprovalState(issue.id, {
              stage: result.stage,
              status: 'error',
              output: { error: result.message ?? 'Pipeline failed without completing' },
              requestedAt: new Date().toISOString(),
            });
          } catch (stateErr) {
            log.error('Failed to set error approval state', {
              issueNumber: issue.number,
              error: stateErr instanceof Error ? stateErr.message : String(stateErr),
            });
          }
          try {
            updateIssueStatus?.(issue.id, IssueStatus.Blocked);
          } catch (updateErr) {
            log.error('Failed to update issue status to blocked', {
              issueNumber: issue.number,
              error: updateErr instanceof Error ? updateErr.message : String(updateErr),
            });
          }
          try {
            this.eventBus.emit('agent_error', {
              issueId: issue.id,
              projectId,
              error: result.message ?? 'Pipeline failed without completing',
            });
          } catch (emitErr) {
            log.error('Failed to emit agent_error event', {
              issueNumber: issue.number,
              error: emitErr instanceof Error ? emitErr.message : String(emitErr),
            });
          }
        }
      } catch (err) {
        const errorMsg = err instanceof Error ? err.message : String(err);
        const currentIssue = issueRepo.findById(issue.id);
        log.error('Pipeline execution failed', {
          issueNumber: issue.number,
          stage: currentIssue?.stage ?? 'unknown',
          error: errorMsg,
        });
        try {
          issueRepo.setApprovalState(issue.id, {
            stage: currentIssue?.stage ?? Stage.Backlog,
            status: 'error',
            output: { error: errorMsg },
            requestedAt: new Date().toISOString(),
          });
        } catch (stateErr) {
          log.error('Failed to set error approval state', {
            issueNumber: issue.number,
            error: stateErr instanceof Error ? stateErr.message : String(stateErr),
          });
        }
        try {
          updateIssueStatus?.(issue.id, IssueStatus.Blocked);
        } catch (updateErr) {
          log.error('Failed to update issue status to blocked', {
            issueNumber: issue.number,
            error: updateErr instanceof Error ? updateErr.message : String(updateErr),
          });
        }
        try {
          this.eventBus.emit('agent_error', {
            issueId: issue.id,
            projectId,
            error: errorMsg,
          });
        } catch (emitErr) {
          log.error('Failed to emit agent_error event', {
            issueNumber: issue.number,
            error: emitErr instanceof Error ? emitErr.message : String(emitErr),
          });
        }
      } finally {
        const agent = this.activeAgents.get(issue.id);
        if (agent) agent.childProcess = undefined;
        this.activeAgents.delete(issue.id);
        this.clearWaiting(issue.id);

        if (conflictResolutionInitiated && deferredRestartWorktreePath) {
          const refreshedIssue = issueRepo.findById(issue.id);
          if (refreshedIssue) {
            log.info('Executing deferred pipeline restart for conflict resolution', {
              issueNumber: issue.number,
            });
            this.startPipeline(
              refreshedIssue,
              projectId,
              issueRepo,
              deferredRestartWorktreePath,
              {
                ...acpOptions,
                cwd: deferredRestartWorktreePath,
              },
              updateIssueStatus,
            );
          }
        }
      }
    })();

    this.activeAgents.set(issue.id, {
      issueId: issue.id,
      issueNumber: issue.number,
      promise,
      projectId,
      progress,
    });
  }
}
