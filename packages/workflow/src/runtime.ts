import type { TaskRunState, WorkflowStageId, WorkflowWork } from './model';

export interface WorkflowIssue {
  id: string;
  number: number;
  title: string;
  stage: WorkflowStageId;
  status: string;
  projectId: string;
}

export interface WorkflowEventBus {
  emit(event: string, data: unknown): void;
}

export interface StageRunResult {
  success: boolean;
  output: unknown;
  checkResults: CheckResult[];
  message?: string;
}

export interface StageContext {
  issue: WorkflowIssue;
  acpOptions: { cwd?: string } & object;
  artifactManager: {
    getChangeDir(issueNumber: number): string | null;
    createChangeDir(issueNumber: number, title: string): string | null;
  };
  eventBus: WorkflowEventBus;
  workflowRun?: object;
  requestedWork?: WorkflowWork;
  requestedTask?: TaskRunState;
  worktreeManager?: unknown;
  projectRepo?: unknown;
  checkpointManager?: unknown;
  issueRepo?: unknown;
  emit: (event: string, data: unknown) => void;
  log: (eventType: string, data: object) => void;
  [key: string]: unknown;
}

export interface CheckResult {
  name: string;
  status: 'pass' | 'fail' | 'error' | 'pending';
  message?: string;
  output?: unknown;
}

export interface CheckContext {
  issue: WorkflowIssue;
  changeDir: string;
  eventBus?: WorkflowEventBus;
  projectId?: string;
  acpOptions: { cwd?: string } & object;
  worktreeManager?: {
    getPath?(projectName: string, issueNumber: number): string | null;
    getHeadSha?(worktreePath: string): Promise<string>;
    isWorktreeClean?(worktreePath: string): Promise<boolean>;
    checkSquashMergeability?(
      projectPath: string,
      projectName: string,
      issueNumber: number,
      baseBranch?: string,
    ): Promise<{
      kind: 'merge-ready';
      strategy: 'squash';
      targetBranch: string;
      baseSha: string;
      candidateHeadSha: string;
      mergeBaseSha: string;
      canMerge: boolean;
      conflictFiles: string[];
      checkedAt: string;
      error?: string;
    }>;
  };
  projectRepo?: {
    findById(id: string): { id: string; name: string; path: string; baseBranch: string } | null;
  };
  [key: string]: unknown;
}

export interface StageTaskResult {
  taskId: string;
  title: string;
  status: 'completed' | 'failed' | 'skipped';
  artifacts: string[];
  events?: string[];
  output?: unknown;
  attemptEvidence?: {
    executionId?: string;
    acpSessionId?: string;
    coderSessionId?: string;
    processPid?: number;
  };
  attempts: number;
  duration: number;
  reason?: string;
  causedBy?: StageTaskCause;
  alreadyReported?: boolean;
  failureCategory?: string;
}

export interface StageTaskCause {
  type: 'check-failure' | 'task-failure' | 'branch-changed' | 'conflict' | 'retry' | 'user-action' | 'system-policy';
  checkName?: string;
  taskId?: string;
  message?: string;
}
