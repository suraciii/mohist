import type { Stage, Issue, Project, IssueStatus } from '../types';
import type { TasksFile } from '../artifacts/change-artifacts-manager';
import type { AcpConnectionOptions } from '../agent-runtime/acp-session';
import type { EventBus } from '../services/event-bus';
import type { WorkflowLogRepo } from '../db/workflow-log-repo';
import type { SessionStreamLogRepo } from '../db/session-stream-log-repo';
import type { CoderSessionRepo } from '../db/coder-session-repo';

export interface ChangeArtifactsManager {
  getChangeDir(issueNumber: number): string | null;
  createChangeDir(issueNumber: number, title: string): string | null;
  readArtifact(changeDir: string, artifactPath: string): string | null;
  writeArtifact(changeDir: string, artifactPath: string, content: string): boolean;
  exists(changeDir: string): boolean;
  readTasks(issueNumber: number): TasksFile | null;
  updateTaskPasses(issueNumber: number, taskId: string, passes: boolean, error?: string | null): boolean;
  archiveChange(issueNumber: number): Promise<void>;
}

export interface IssueRepo {
  updateStage(id: string, stage: Stage): Issue | null;
  setApprovalState(id: string, state: { stage: Stage; status: string; output: unknown; requestedAt: string }): void;
  clearApprovalState(id: string): void;
  updateStatus(id: string, status: IssueStatus): Issue | null;
}

export interface StageRunResult {
  success: boolean;
  requiresApproval: boolean;
  output: unknown;
  message?: string;
  nextStage?: Stage;
  escalateToStage?: Stage;
}

export interface StageContext {
  issue: Issue;
  acpOptions: AcpConnectionOptions;
  artifactManager: ChangeArtifactsManager;
  worktreeManager: WorktreeManager;
  projectRepo: ProjectRepo;
  eventBus: EventBus;
  checkpointManager: CheckpointManager;
  issueRepo: IssueRepo;
  workflowLogRepo?: WorkflowLogRepo;
  sessionStreamLogRepo?: SessionStreamLogRepo;
  coderSessionRepo?: CoderSessionRepo;
  signal?: AbortSignal;
}

export interface CheckResult {
  name: string;
  status: 'pass' | 'fail' | 'error';
  message?: string;
  output?: unknown;
}

export interface CheckContext {
  issue: Issue;
  changeDir: string;
  eventBus?: EventBus;
  projectId?: string;
  acpOptions: AcpConnectionOptions;
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
  mergeBack(projectPath: string, projectName: string, issueNumber: number, baseBranch: string): Promise<{ success: boolean; message: string }>;
  create(projectPath: string, projectName: string, issueNumber: number, baseBranch?: string): Promise<string>;
  list(projectPath: string): Promise<{ worktreePath: string; branch: string; issueNumber: number }[]>;
  getWorktreeStatus(projectPath: string, projectName: string, issueNumber: number): Promise<{ exists: boolean; branch: string; baseBranch?: string; ahead: number; behind: number; canFastForward: boolean; isRebaseInProgress: boolean; rebaseInProgress?: boolean; conflictingFiles?: string[] }>;
  prune(projectPath: string): Promise<void>;
}

export interface ProjectRepo {
  findById(id: string): Project | null;
}

import type { CheckpointManager as CheckpointManagerInterface } from './checkpoint-manager';

export type CheckpointManager = CheckpointManagerInterface;
