import type { Stage, Issue, Project, IssueStatus } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import type { AgentSessionOptions } from '../agent-runtime/agent-session';
import type { MergeBackResult, MergeMetadata } from '../git/worktree-manager';
import type { EventBus } from '../services/event-bus';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';
import type { WorkflowSessionObserverDeps } from '../services/session-observers';
import type { StageStateService } from '../services/stage-state-service';

type StageType = Stage;

export interface ChangeArtifactsManager {
  getChangeDir(issueNumber: number): string | null;
  createChangeDir(issueNumber: number, title: string): string | null;
  readArtifact(changeDir: string, artifactPath: string): string | null;
  writeArtifact(changeDir: string, artifactPath: string, content: string): boolean;
  exists(changeDir: string): boolean;
  readTasks(issueNumber: number): TasksFile | null;
  updateTaskPasses(issueNumber: number, taskId: string, passes: boolean, error?: string | null): boolean;
  syncTasksToStageState(issueNumber: number, issueId: string, stage: StageType, stageStateService: StageStateService): void;
  archiveChange(issueNumber: number): Promise<void>;
}

export interface IssueRepo {
  updateStage(id: string, stage: Stage): Issue | null;
  setApprovalState(id: string, state: { stage: Stage; status: string; output: unknown; requestedAt: string }): void;
  clearApprovalState(id: string): void;
  updateStatus(id: string, status: IssueStatus): Issue | null;
  findById(id: string): Issue | null;
  setMergeState?(id: string, mergeState: string): Issue | null;
}

export interface StageRunResult {
  success: boolean;
  output: unknown;
  checkResults: CheckResult[];
  message?: string;
  nextStage?: Stage;
}

export interface StageContext {
  issue: Issue;
  acpOptions: AgentSessionOptions;
  artifactManager: ChangeArtifactsManager;
  worktreeManager: WorktreeManager;
  projectRepo: ProjectRepo;
  eventBus: EventBus;
  checkpointManager: CheckpointManager;
  issueRepo: IssueRepo;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo?: CoderSessionRepo;
  stageExecutionRepo?: StageExecutionRepo;
  checkSuiteRepo?: CheckSuiteRepo;
  stageStateService?: StageStateService;
  signal?: AbortSignal;
}

export interface CheckResult {
  name: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  message?: string;
  output?: unknown;
}

export interface CheckContext {
  issue: Issue;
  changeDir: string;
  eventBus?: EventBus;
  projectId?: string;
  acpOptions: AgentSessionOptions;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo?: CoderSessionRepo;
  worktreeManager?: WorktreeManager;
  projectRepo?: ProjectRepo;
  createWorkflowSessionObservers?: (options: WorkflowSessionObserverDeps) => import('../agent-runtime/session-observer').SessionObserver[];
}

export interface WorktreeManager {
  canFastForward(projectPath: string, projectName: string, issueNumber: number, baseBranch: string): Promise<boolean>;
  rebaseOntoMaster(projectPath: string, projectName: string, issueNumber: number, baseBranch: string, options?: { abortOnConflict?: boolean }): Promise<{ success: boolean; conflicts: string[] }>;
  getPath(projectName: string, issueNumber: number): string | null;
  exists(projectName: string, issueNumber: number): boolean;
  remove(projectPath: string, projectName: string, issueNumber: number): Promise<void>;
  findWipCommit(worktreePath: string, taskId: string): Promise<{ hash: string; message: string; changedFiles: string[]; diffStat: string } | null>;
  createWipCommit(worktreePath: string, taskId: string, attemptNumber: number): Promise<string | null>;
  abortRebase(projectName: string, issueNumber: number): Promise<void>;
  isRebaseInProgress(projectName: string, issueNumber: number): Promise<boolean>;
  mergeBack(projectPath: string, projectName: string, issueNumber: number, baseBranch: string, metadata: MergeMetadata): Promise<MergeBackResult>;
  create(projectPath: string, projectName: string, issueNumber: number, baseBranch?: string): Promise<string>;
  list(projectPath: string): Promise<{ worktreePath: string; branch: string; issueNumber: number }[]>;
  getWorktreeStatus(projectPath: string, projectName: string, issueNumber: number): Promise<{ exists: boolean; branch: string; baseBranch?: string; ahead: number; behind: number; canFastForward: boolean; isRebaseInProgress: boolean; rebaseInProgress?: boolean; conflictingFiles?: string[] }>;
  prune(projectPath: string): Promise<void>;
  mergeApprovedCandidate(projectPath: string, projectName: string, issueNumber: number, baseBranch?: string, metadata?: MergeMetadata): Promise<{ targetBranch: string; baseSha: string; candidateHeadSha: string; landedSha: string; rebased?: boolean } | { failingStep: 'merge'; targetBranch: string; baseSha: string; candidateHeadSha: string; conflictFiles?: string[]; error: string }>;
  getHeadSha(worktreePath: string): Promise<string>;
  isWorktreeClean(worktreePath: string): Promise<boolean>;
  createCheckConvergenceCommit(worktreePath: string, issueNumber: number): Promise<import('../git/worktree-manager').ConvergenceCommitResult>;
}

export interface ProjectRepo {
  findById(id: string): Project | null;
}

import type { CheckpointManager as CheckpointManagerInterface } from './checkpoint-manager';
import type { StageExecutionRepo } from '../db/stage-execution-repo';

export type CheckpointManager = CheckpointManagerInterface;

export interface StageTask {
  id: string;
  title: string;
  status: 'pending' | 'running' | 'completed' | 'failed';
  order: number;
  dependsOn: string[];
  source: 'static' | 'dynamic';
  artifacts: string[];
  attempts: number;
  maxAttempts: number;
  startedAt?: string;
  completedAt?: string;
}

export interface StageTaskResult {
  taskId: string;
  title: string;
  status: 'completed' | 'failed' | 'skipped';
  artifacts: string[];
  output?: unknown;
  attempts: number;
  duration: number;
}

export interface CheckFailurePolicy {
  checkName: string;
  fixTaskId: string;
  maxAttempts: number;
}

export interface AuthoritativeAiReviewResult {
  verdict: string;
  reviewReport?: string;
  snapshotSha?: string;
  reviewArtifactPath?: string;
  selfCheckArtifactPath?: string;
  convergedAt?: string;
}

export interface AuthoritativeAiReviewOptions {
  snapshotSha?: string;
  reviewArtifactPath?: string;
  selfCheckArtifactPath?: string;
}

export function getLatestCheckResult(results: CheckResult[], name: string): CheckResult | undefined {
  for (let i = results.length - 1; i >= 0; i--) {
    if (results[i].name === name) {
      return results[i];
    }
  }
  return undefined;
}

export function replaceCurrentAiReviewTruth(results: CheckResult[]): CheckResult[] {
  const latest = getLatestCheckResult(results, 'ai-review');
  if (!latest) return results;
  const filtered = results.filter(r => r.name !== 'ai-review');
  filtered.push(latest);
  return filtered;
}

export function buildAuthoritativeAiReviewResult(
  checkResult: CheckResult,
  options?: AuthoritativeAiReviewOptions,
): AuthoritativeAiReviewResult | null {
  if (checkResult.name !== 'ai-review') return null;
  const output = checkResult.output as Record<string, unknown> | undefined;
  if (!output) return null;

  return {
    verdict: output.verdict as string,
    reviewReport: output.reviewReport as string | undefined,
    snapshotSha: options?.snapshotSha ?? (output.snapshotSha as string | undefined),
    reviewArtifactPath: options?.reviewArtifactPath ?? (output.reviewArtifactPath as string | undefined),
    selfCheckArtifactPath: options?.selfCheckArtifactPath ?? (output.selfCheckArtifactPath as string | undefined),
    convergedAt: new Date().toISOString(),
  };
}

export interface CheckSuiteRepo {
  findActiveByIssueId(issueId: string): import('../types').CheckSuite | null;
  updateChecks(suiteId: string, checkName: string, checkState: import('../types').CheckState): import('../types').CheckSuite | null;
  updateSnapshotSha(suiteId: string, newSha: string): import('../types').CheckSuite | null;
  updateSnapshotShaPreservingChecks(suiteId: string, newSha: string): import('../types').CheckSuite | null;
}

export function emitStageTaskUpdate(
  eventBus: import('../services/event-bus').EventBus | undefined,
  issueId: string,
  projectId: string,
  stage: string,
  taskId: string,
  taskTitle: string,
  status: 'started' | 'completed' | 'failed' | 'retrying',
  attempt: number,
  artifacts: string[],
): void {
  if (!eventBus) return;
  try {
    eventBus.emit('stage_task_update', {
      issueId,
      projectId,
      stage,
      taskId,
      taskTitle,
      status,
      attempt,
      artifacts,
    });
  } catch {
    // fire-and-forget
  }
}
