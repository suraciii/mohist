import { describe, expect, it, beforeEach } from 'vitest';
import { evaluateBaseDrift, type BaseDriftInput, type GitFacts, type CandidateEvidence, type WorkflowFacts } from '../src/services/base-drift-service';
import { Stage } from '../src/types';
import type { WorkflowRunSnapshot, StageRunSnapshot } from '@mohist/workflow/internal/model';

function makeGitFacts(overrides: Partial<GitFacts> = {}): GitFacts {
  return {
    currentBaseSha: 'base-current',
    candidateHeadSha: 'head-candidate',
    mergeBaseSha: 'merge-base',
    ...overrides,
  };
}

function makeCandidateEvidence(overrides: Partial<CandidateEvidence> = {}): CandidateEvidence {
  return {
    observedBaseSha: null,
    mergeReadySnapshot: null,
    approvalSnapshot: null,
    rebaseTaskOutput: null,
    reviewCheckOutput: null,
    mergeReadyCheckOutput: null,
    ...overrides,
  };
}

function makeWorkflowFacts(overrides: Partial<WorkflowFacts> = {}): WorkflowFacts {
  return {
    workflowRun: null,
    currentStage: null,
    isRunning: false,
    runningTaskId: null,
    ...overrides,
  };
}

function makeWorkflowRunSnapshot(currentStage: Stage, stageOverrides: Partial<StageRunSnapshot> = {}): WorkflowRunSnapshot {
  return {
    id: 'run-1',
    issueId: 'issue-1',
    issueNumber: 188,
    status: 'running',
    currentStage,
    stageOrder: [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate],
    stageRuns: [makeStageRunSnapshot(currentStage, stageOverrides)],
    failure: null,
  };
}

function makeStageRunSnapshot(stage: Stage, overrides: Partial<StageRunSnapshot> = {}): StageRunSnapshot {
  return {
    stage,
    status: 'running',
    order: 2,
    tasks: [],
    checks: [],
    approval: null,
    failure: null,
    commitPoint: null,
    ...overrides,
  };
}

function makeInput(overrides: Partial<BaseDriftInput> = {}): BaseDriftInput {
  return {
    projectId: 'proj-1',
    issueId: 'issue-1',
    issueNumber: 188,
    baseBranch: 'main',
    gitFacts: makeGitFacts(),
    candidateEvidence: makeCandidateEvidence(),
    workflowFacts: makeWorkflowFacts(),
    ...overrides,
  };
}

describe('T-003 regression: base advanced during Build task', () => {
  describe('drifted issue with running mutating Build task defers rebase', () => {
    it('defers rebase when Build stage has a running task', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Build, {
        status: 'running',
        tasks: [
          {
            id: 'T-001',
            title: 'Build task 1',
            status: 'running',
            order: 0,
            dependsOn: [],
            attempts: 1,
            duration: 0,
            artifacts: [],
            output: null,
            reason: null,
            causedBy: null,
          },
        ],
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Build,
          isRunning: true,
          runningTaskId: 'T-001',
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('defer');
      expect(result.deferReason).toBe('task-running');
      expect(result.safeWindow).toBe(false);
    });

    it('does not append rebase-branch when task is running', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Build, {
        status: 'running',
        tasks: [
          {
            id: 'T-001',
            title: 'Build task 1',
            status: 'running',
            order: 0,
            dependsOn: [],
            attempts: 1,
            duration: 0,
            artifacts: [],
            output: null,
            reason: null,
            causedBy: null,
          },
        ],
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Build,
          isRunning: true,
          runningTaskId: 'T-001',
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.decision).not.toBe('enqueue');
    });
  });

  describe('deferred rebase becomes actionable at task boundary', () => {
    it('returns enqueue when Build task completes and stage is idle', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Build, {
        status: 'running',
        tasks: [
          {
            id: 'T-001',
            title: 'Build task 1',
            status: 'completed',
            order: 0,
            dependsOn: [],
            attempts: 1,
            duration: 5000,
            artifacts: [],
            output: null,
            reason: null,
            causedBy: null,
          },
        ],
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Build,
          isRunning: false,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('enqueue');
      expect(result.safeWindow).toBe(true);
    });

    it('returns enqueue when at approval wait in Plan stage', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Plan, {
        status: 'awaiting-approval',
        tasks: [],
        approval: {
          status: 'awaiting',
          output: null,
          requestedAt: new Date().toISOString(),
          respondedAt: null,
        },
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Plan,
          isRunning: false,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('enqueue');
      expect(result.safeWindow).toBe(true);
    });

    it('returns enqueue when at approval wait in Check stage', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Check, {
        status: 'awaiting-approval',
        tasks: [],
        approval: {
          status: 'awaiting',
          output: null,
          requestedAt: new Date().toISOString(),
          respondedAt: null,
        },
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: false,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('enqueue');
      expect(result.safeWindow).toBe(true);
    });
  });

  describe('rebase deduplication', () => {
    it('returns defer when rebase-branch is already pending', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Check, {
        status: 'running',
        tasks: [
          {
            id: 'rebase-branch',
            title: 'Rebase branch',
            status: 'pending',
            order: 0,
            dependsOn: [],
            attempts: 0,
            duration: 0,
            artifacts: [],
            output: null,
            reason: null,
            causedBy: null,
          },
        ],
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: false,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('defer');
      expect(result.deferReason).toBe('rebase-already-pending');
    });

    it('returns defer when rebase-branch is already running', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Check, {
        status: 'running',
        tasks: [
          {
            id: 'rebase-branch',
            title: 'Rebase branch',
            status: 'running',
            order: 0,
            dependsOn: [],
            attempts: 1,
            duration: 0,
            artifacts: [],
            output: null,
            reason: null,
            causedBy: null,
          },
        ],
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: true,
          runningTaskId: 'rebase-branch',
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('defer');
    });
  });

  describe('stale evidence invalidation at safe window', () => {
    it('marks merge-ready and approval stale when Check drifted at safe window', () => {
      const workflowRun = makeWorkflowRunSnapshot(Stage.Check, {
        status: 'awaiting-approval',
        tasks: [],
        approval: {
          status: 'awaiting',
          output: null,
          requestedAt: new Date().toISOString(),
          respondedAt: null,
        },
      });

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: 'old-base',
          mergeReadySnapshot: {
            kind: 'merge-ready',
            strategy: 'squash',
            targetBranch: 'main',
            baseSha: 'old-base',
            candidateHeadSha: 'head-candidate',
            mergeBaseSha: 'merge-base',
            canMerge: true,
            conflictFiles: [],
            checkedAt: new Date().toISOString(),
          },
          approvalSnapshot: {
            status: 'approved',
            output: { baseSha: 'old-base' },
            requestedAt: new Date().toISOString(),
            respondedAt: new Date().toISOString(),
          },
        }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: false,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.staleEvidence).toEqual({
        review: false,
        mergeReady: true,
        approval: true,
      });
      expect(result.decision).toBe('needs-attention');
    });
  });
});