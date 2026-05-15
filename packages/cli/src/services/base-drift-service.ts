import type { MergeabilitySnapshot } from '../git/worktree-manager';
import type { WorkflowRunSnapshot, StageRunSnapshot } from '../workflow/domain';
import type { StageApprovalState } from './stage-state-service';
import type { EventBus } from './event-bus';
import type { WorkflowRunWithStageRuns } from '../db/workflow-run-repo';
import type { WorkflowApplicationService } from './workflow-application-service';
import { Stage, IssueStatus, MergeState } from '../types';
import { Log } from '../util/log';

const log = Log.create({ service: 'base-drift-service' });

export type RebaseDecision = 'skip' | 'suggest' | 'enqueue' | 'defer' | 'needs-attention';

export type DeferReason = 'agent-running' | 'task-running' | 'waiting-for-task-boundary' | 'rebase-already-pending';

export interface StaleEvidence {
  review: boolean;
  mergeReady: boolean;
  approval: boolean;
}

export interface BaseDriftState {
  drifted: boolean;
  baseBranch: string;
  observedBaseSha: string | null;
  currentBaseSha: string | null;
  candidateHeadSha: string | null;
  mergeBaseSha: string | null;
  decision: RebaseDecision;
  safeWindow: boolean;
  deferReason?: DeferReason;
  staleEvidence?: StaleEvidence;
  conflicts?: string[];
  message: string;
}

export interface GitFacts {
  currentBaseSha: string | null;
  candidateHeadSha: string | null;
  mergeBaseSha: string | null;
}

export interface RebaseTaskOutput {
  beforeBaseSha: string;
  afterBaseSha: string;
  beforeHeadSha: string;
  afterHeadSha: string;
  shaChanged: boolean;
  conflicts?: string[];
}

export interface CandidateEvidence {
  observedBaseSha: string | null;
  mergeReadySnapshot: MergeabilitySnapshot | null;
  approvalSnapshot: StageApprovalState | null;
  rebaseTaskOutput: RebaseTaskOutput | null;
  reviewCheckOutput: unknown;
  mergeReadyCheckOutput: unknown;
}

export interface WorkflowFacts {
  workflowRun: WorkflowRunSnapshot | null;
  currentStage: string | null;
  isRunning: boolean;
  runningTaskId: string | null;
}

export interface BaseDriftInput {
  projectId: string;
  issueId: string;
  issueNumber: number;
  baseBranch: string;
  gitFacts: GitFacts;
  candidateEvidence: CandidateEvidence;
  workflowFacts: WorkflowFacts;
}

function deriveObservedBaseFromEvidence(evidence: CandidateEvidence): string | null {
  if (evidence.observedBaseSha) {
    return evidence.observedBaseSha;
  }
  if (evidence.rebaseTaskOutput?.afterBaseSha) {
    return evidence.rebaseTaskOutput.afterBaseSha;
  }
  if (evidence.mergeReadySnapshot?.baseSha) {
    return evidence.mergeReadySnapshot.baseSha;
  }
  if (evidence.approvalSnapshot?.output && typeof evidence.approvalSnapshot.output === 'object') {
    const output = evidence.approvalSnapshot.output as Record<string, unknown>;
    if (typeof output.baseSha === 'string' && output.baseSha) {
      return output.baseSha;
    }
  }
  return null;
}

function detectStaleEvidence(
  evidence: CandidateEvidence,
  observedBaseSha: string | null,
  currentBaseSha: string | null,
): StaleEvidence | undefined {
  if (!observedBaseSha || !currentBaseSha || observedBaseSha === currentBaseSha) {
    return undefined;
  }

  const staleReview = observedBaseSha !== currentBaseSha && evidence.reviewCheckOutput != null;
  const staleMergeReady =
    observedBaseSha !== currentBaseSha &&
    evidence.mergeReadySnapshot != null &&
    evidence.mergeReadySnapshot.baseSha !== currentBaseSha;
  const staleApproval =
    observedBaseSha !== currentBaseSha && evidence.approvalSnapshot?.status === 'approved';

  if (!staleReview && !staleMergeReady && !staleApproval) {
    return undefined;
  }

  return {
    review: staleReview,
    mergeReady: staleMergeReady,
    approval: staleApproval,
  };
}

function isSafeWindow(workflowFacts: WorkflowFacts): boolean {
  if (!workflowFacts.workflowRun) return false;

  const snapshot = workflowFacts.workflowRun;
  const currentStageRun = snapshot.stageRuns.find(sr => sr.stage === snapshot.currentStage);

  if (!currentStageRun) return false;

  if (currentStageRun.status === 'awaiting-approval') {
    return true;
  }

  if (currentStageRun.status !== 'running') {
    return false;
  }

  if (workflowFacts.runningTaskId) {
    return false;
  }

  const nextTask = currentStageRun.tasks.find(t => t.status === 'pending');
  if (!nextTask) {
    return true;
  }

  return false;
}

function determineDeferReason(workflowFacts: WorkflowFacts, rebaseAlreadyPending: boolean): DeferReason {
  if (rebaseAlreadyPending) {
    return 'rebase-already-pending';
  }
  if (workflowFacts.runningTaskId) {
    return 'task-running';
  }
  return 'agent-running';
}

function extractConflictsFromRebaseTask(stageRun: StageRunSnapshot | undefined): string[] | undefined {
  if (!stageRun) return undefined;

  const rebaseTask = stageRun.tasks.find(t => t.id === 'rebase-branch' && t.status === 'completed');
  if (!rebaseTask?.output || typeof rebaseTask.output !== 'object') return undefined;

  const output = rebaseTask.output as Record<string, unknown>;
  if (Array.isArray(output.conflicts)) {
    return output.conflicts.filter((c): c is string => typeof c === 'string');
  }

  return undefined;
}

export function evaluateBaseDrift(input: BaseDriftInput): BaseDriftState {
  const { baseBranch, gitFacts, candidateEvidence, workflowFacts } = input;
  const { currentBaseSha, candidateHeadSha, mergeBaseSha } = gitFacts;

  const observedBaseSha = deriveObservedBaseFromEvidence(candidateEvidence);

  if (!observedBaseSha || !currentBaseSha) {
    return {
      drifted: false,
      baseBranch,
      observedBaseSha,
      currentBaseSha,
      candidateHeadSha,
      mergeBaseSha,
      decision: 'skip',
      safeWindow: false,
      message: 'Cannot determine drift: missing base observation or current base SHA',
    };
  }

  const drifted = observedBaseSha !== currentBaseSha;

  if (!drifted) {
    return {
      drifted: false,
      baseBranch,
      observedBaseSha,
      currentBaseSha,
      candidateHeadSha,
      mergeBaseSha,
      decision: 'skip',
      safeWindow: false,
      message: 'Candidate is aligned with current base',
    };
  }

  const currentStageRun = workflowFacts.workflowRun?.stageRuns.find(
    sr => sr.stage === workflowFacts.workflowRun!.currentStage,
  );
  const rebaseAlreadyPending =
    currentStageRun?.tasks.some(t => t.id === 'rebase-branch' && t.status === 'pending') ?? false;

  const safeWindow = isSafeWindow(workflowFacts);
  const staleEvidence = detectStaleEvidence(candidateEvidence, observedBaseSha, currentBaseSha);

  if (!safeWindow) {
    const deferReason = determineDeferReason(workflowFacts, rebaseAlreadyPending);
    return {
      drifted: true,
      baseBranch,
      observedBaseSha,
      currentBaseSha,
      candidateHeadSha,
      mergeBaseSha,
      decision: 'defer',
      safeWindow: false,
      deferReason,
      staleEvidence,
      message: `Candidate is behind base; rebase deferred until safe window (${deferReason})`,
    };
  }

  if (rebaseAlreadyPending) {
    return {
      drifted: true,
      baseBranch,
      observedBaseSha,
      currentBaseSha,
      candidateHeadSha,
      mergeBaseSha,
      decision: 'defer',
      safeWindow: true,
      deferReason: 'rebase-already-pending',
      staleEvidence,
      message: 'Rebase already pending; waiting for current rebase to complete',
    };
  }

  if (staleEvidence?.approval) {
    return {
      drifted: true,
      baseBranch,
      observedBaseSha,
      currentBaseSha,
      candidateHeadSha,
      mergeBaseSha,
      decision: 'needs-attention',
      safeWindow: true,
      staleEvidence,
      message: 'Stale approval evidence detected; user must rebase or rerun checks before approving',
    };
  }

  const conflicts = extractConflictsFromRebaseTask(currentStageRun);

  return {
    drifted: true,
    baseBranch,
    observedBaseSha,
    currentBaseSha,
    candidateHeadSha,
    mergeBaseSha,
    decision: 'enqueue',
    safeWindow: true,
    staleEvidence,
    conflicts,
    message: 'Candidate has drifted from base; safe rebase window available',
  };
}

export class BaseDriftService {
  private lastScannedBaseSha: string | null = null;

  evaluate(input: BaseDriftInput): BaseDriftState {
    return evaluateBaseDrift(input);
  }

  async scanActiveCandidatesForDrift(input: {
    projectId: string;
    baseBranch: string;
    newBaseSha: string;
    issueRepo: { findAll: (options: { projectId: string; status?: IssueStatus; stage?: Stage }) => Array<{ id: string; number: number; stage: Stage; status: IssueStatus; mergeState?: string }> };
    workflowRunService: { getLatestRunForIssue: (issueId: string) => WorkflowRunWithStageRuns | null };
    worktreeManager: { getPath: (projectName: string, issueNumber: number) => string | null; getHeadSha: (worktreePath: string) => Promise<string | null>; resolveRefSha?: (projectPath: string, ref: string) => Promise<string> };
    project: { path: string; name: string };
    eventBus: EventBus;
    workflowApplicationService?: WorkflowApplicationService;
  }): Promise<{ scannedCount: number; driftResults: Map<string, BaseDriftState> }> {
    const { projectId, baseBranch, newBaseSha, issueRepo, workflowRunService, worktreeManager, project, eventBus, workflowApplicationService } = input;

    const driftResults = new Map<string, BaseDriftState>();

    if (this.lastScannedBaseSha === newBaseSha) {
      log.info('Scan skipped: already scanned for this base SHA', { projectId, newBaseSha });
      return { scannedCount: 0, driftResults };
    }

    this.lastScannedBaseSha = newBaseSha;

    const activeStages: Stage[] = [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate];
    const skippedStatuses: IssueStatus[] = [IssueStatus.Closed, IssueStatus.Completed];

    const activeIssues = issueRepo.findAll({ projectId, status: IssueStatus.Active });
    const candidates = activeIssues.filter(issue => {
      if (!activeStages.includes(issue.stage)) return false;
      if (issue.mergeState === MergeState.Merged) return false;
      if (skippedStatuses.includes(issue.status)) return false;
      return true;
    });

    log.info('Scanning active candidates for base drift', {
      projectId,
      baseBranch,
      newBaseSha,
      candidateCount: candidates.length,
    });

    for (const issue of candidates) {
      try {
        const worktreePath = worktreeManager.getPath(project.name, issue.number);
        const candidateHeadSha = worktreePath ? await worktreeManager.getHeadSha(worktreePath) : null;

        const workflowRun = workflowRunService.getLatestRunForIssue(issue.id);
        const workflowFacts: WorkflowFacts = {
          workflowRun: workflowRun as WorkflowRunSnapshot | null,
          currentStage: workflowRun?.currentStage ?? null,
          isRunning: false,
          runningTaskId: null,
        };

        const candidateEvidence: CandidateEvidence = {
          observedBaseSha: null,
          mergeReadySnapshot: null,
          approvalSnapshot: null,
          rebaseTaskOutput: null,
          reviewCheckOutput: null,
          mergeReadyCheckOutput: null,
        };

        const driftInput: BaseDriftInput = {
          projectId,
          issueId: issue.id,
          issueNumber: issue.number,
          baseBranch,
          gitFacts: {
            currentBaseSha: newBaseSha,
            candidateHeadSha,
            mergeBaseSha: null,
          },
          candidateEvidence,
          workflowFacts,
        };

        const driftState = evaluateBaseDrift(driftInput);
        driftResults.set(issue.id, driftState);

        eventBus.emit('base_drift_detected', {
          projectId,
          issueId: issue.id,
          issueNumber: issue.number,
          drifted: driftState.drifted,
          observedBaseSha: driftState.observedBaseSha,
          currentBaseSha: driftState.currentBaseSha,
          decision: driftState.decision,
        });

        if (driftState.drifted) {
          eventBus.emit('rebase_opportunity_opened', {
            projectId,
            issueId: issue.id,
            issueNumber: issue.number,
            decision: driftState.decision,
            safeWindow: driftState.safeWindow,
            deferReason: driftState.deferReason,
          });

          if (driftState.deferReason) {
            eventBus.emit('active_work_protected', {
              projectId,
              issueId: issue.id,
              issueNumber: issue.number,
              deferReason: driftState.deferReason,
            });
          }

          if (driftState.staleEvidence) {
            eventBus.emit('candidate_evidence_invalidated', {
              projectId,
              issueId: issue.id,
              issueNumber: issue.number,
              staleEvidence: driftState.staleEvidence,
            });
          }

          if (driftState.decision === 'needs-attention') {
            eventBus.emit('user_attention_requested', {
              projectId,
              issueId: issue.id,
              issueNumber: issue.number,
              reason: driftState.message,
              suggestion: 'Rebase or rerun checks before approving',
            });
          }

          if (driftState.decision === 'enqueue' && workflowApplicationService && driftState.observedBaseSha && driftState.currentBaseSha) {
            try {
              workflowApplicationService.scheduleRebaseForDrift({
                issueId: issue.id,
                baseBranch,
                observedBaseSha: driftState.observedBaseSha,
                currentBaseSha: driftState.currentBaseSha,
              });
              eventBus.emit('rebase_task_scheduled', {
                projectId,
                issueId: issue.id,
                issueNumber: issue.number,
                reason: driftState.message,
              });
            } catch (err) {
              log.warn('Failed to schedule rebase task for drifted issue', {
                issueId: issue.id,
                issueNumber: issue.number,
                error: err instanceof Error ? err.message : String(err),
              });
            }
          }
        }
      } catch (err) {
        log.warn('Failed to evaluate drift for issue', {
          issueId: issue.id,
          issueNumber: issue.number,
          error: err instanceof Error ? err.message : String(err),
        });
      }
    }

    log.info('Base drift scan complete', {
      projectId,
      scannedCount: candidates.length,
      driftedCount: Array.from(driftResults.values()).filter(d => d.drifted).length,
    });

    return { scannedCount: candidates.length, driftResults };
  }
}