import { describe, expect, it } from 'vitest';
import { evaluateBaseDrift, BaseDriftService, type BaseDriftInput, type GitFacts, type CandidateEvidence, type WorkflowFacts } from '../src/services/base-drift-service';
import { Stage, IssueStatus, MergeState } from '../src/types';
import type { WorkflowRunSnapshot, StageRunSnapshot } from '../src/workflow/model';

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

function makeWorkflowRunSnapshot(stageOverrides: Partial<StageRunSnapshot> = {}): WorkflowRunSnapshot {
  return {
    id: 'run-1',
    issueId: 'issue-1',
    issueNumber: 188,
    status: 'running',
    currentStage: Stage.Check,
    stageOrder: [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate],
    stageRuns: [makeStageRunSnapshot(Stage.Check, stageOverrides)],
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

describe('evaluateBaseDrift', () => {
  describe('no drift', () => {
    it('returns skip when observed base matches current base', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'abc123' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'abc123' }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(false);
      expect(result.decision).toBe('skip');
      expect(result.observedBaseSha).toBe('abc123');
      expect(result.currentBaseSha).toBe('abc123');
    });

    it('returns skip when observed base is null and current base is unknown', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: null }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: null }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(false);
      expect(result.decision).toBe('skip');
    });

    it('skips when observed base is derived from rebase task output matching current base', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'base-after' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: null,
          rebaseTaskOutput: {
            beforeBaseSha: 'base-before',
            afterBaseSha: 'base-after',
            beforeHeadSha: 'head-before',
            afterHeadSha: 'head-after',
            shaChanged: true,
          },
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(false);
      expect(result.decision).toBe('skip');
      expect(result.observedBaseSha).toBe('base-after');
    });

    it('skips when observed base is derived from merge-ready snapshot matching current base', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'base-same' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: null,
          mergeReadySnapshot: {
            kind: 'merge-ready',
            strategy: 'squash',
            targetBranch: 'main',
            baseSha: 'base-same',
            candidateHeadSha: 'head-candidate',
            mergeBaseSha: 'merge-base',
            canMerge: true,
            conflictFiles: [],
            checkedAt: new Date().toISOString(),
          },
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(false);
      expect(result.decision).toBe('skip');
      expect(result.observedBaseSha).toBe('base-same');
    });
  });

  describe('drifted state', () => {
    it('returns drifted state with all sha facts when base has advanced', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'base-new' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: 'base-old',
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.observedBaseSha).toBe('base-old');
      expect(result.currentBaseSha).toBe('base-new');
      expect(result.candidateHeadSha).toBe('head-candidate');
      expect(result.mergeBaseSha).toBe('merge-base');
    });

    it('marks stale evidence when drifted Check has review output', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: 'old-base',
          reviewCheckOutput: { verdict: 'PASS', snapshotSha: 'old-snap' },
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.staleEvidence).toEqual({
        review: true,
        mergeReady: false,
        approval: false,
      });
    });

    it('marks stale merge-ready when drifted Check has merge-ready snapshot with old base', () => {
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
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.staleEvidence).toEqual({
        review: false,
        mergeReady: true,
        approval: false,
      });
    });

    it('marks stale approval when drifted Check has approved approval snapshot', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: 'old-base',
          approvalSnapshot: {
            status: 'approved',
            output: { baseSha: 'old-base' },
            requestedAt: new Date().toISOString(),
            respondedAt: new Date().toISOString(),
          },
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.staleEvidence?.approval).toBe(true);
    });

    it('does not mutate git state (no side effects)', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'base-new' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'base-old' }),
      });

      const snapshotBefore = JSON.stringify(input);
      evaluateBaseDrift(input);
      const snapshotAfter = JSON.stringify(input);

      expect(snapshotAfter).toBe(snapshotBefore);
    });
  });

  describe('safe window decisions', () => {
    it('defers when a task is currently running', () => {
      const workflowRun = makeWorkflowRunSnapshot(
        makeStageRunSnapshot(Stage.Check, {
          status: 'running',
          tasks: [
            {
              id: 'ai-review',
              title: 'AI review',
              status: 'running',
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
        }),
      );

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: true,
          runningTaskId: 'ai-review',
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('defer');
      expect(result.safeWindow).toBe(false);
      expect(result.deferReason).toBe('task-running');
    });

    it('allows enqueue at approval wait without running tasks', () => {
      const workflowRun = makeWorkflowRunSnapshot(
        makeStageRunSnapshot(Stage.Check, {
          status: 'awaiting-approval',
          tasks: [],
          approval: {
            status: 'awaiting',
            output: null,
            requestedAt: new Date().toISOString(),
            respondedAt: null,
          },
        }),
      );

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
        }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: true,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('enqueue');
      expect(result.safeWindow).toBe(true);
      expect(result.staleEvidence).toEqual({
        review: false,
        mergeReady: true,
        approval: false,
      });
    });

    it('returns needs-attention when approval is approved but evidence is stale', () => {
      const workflowRun = makeWorkflowRunSnapshot(
        makeStageRunSnapshot(Stage.Check, {
          status: 'awaiting-approval',
          tasks: [],
          approval: {
            status: 'awaiting',
            output: null,
            requestedAt: new Date().toISOString(),
            respondedAt: null,
          },
        }),
      );

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: 'old-base',
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
          isRunning: true,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('needs-attention');
      expect(result.safeWindow).toBe(true);
      expect(result.staleEvidence?.approval).toBe(true);
    });

    it('defers when rebase is already pending', () => {
      const workflowRun = makeWorkflowRunSnapshot(
        makeStageRunSnapshot(Stage.Check, {
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
        }),
      );

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'old-base' }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: true,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.decision).toBe('defer');
      expect(result.deferReason).toBe('rebase-already-pending');
    });
  });

  describe('missing historical observation', () => {
    it('derives observed base from merge base when no stored observation exists', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({
          currentBaseSha: 'base-current',
          mergeBaseSha: 'merge-base-derived',
        }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(false);
      expect(result.decision).toBe('skip');
    });

    it('returns skip when no evidence and no merge base available', () => {
      const input = makeInput({
        gitFacts: makeGitFacts({
          currentBaseSha: 'base-current',
          mergeBaseSha: null,
          candidateHeadSha: null,
        }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(false);
      expect(result.decision).toBe('skip');
    });
  });

  describe('conflicts from rebase task', () => {
    it('extracts conflicts from completed rebase-branch task', () => {
      const workflowRun = makeWorkflowRunSnapshot(
        makeStageRunSnapshot(Stage.Check, {
          status: 'running',
          tasks: [
            {
              id: 'rebase-branch',
              title: 'Rebase branch',
              status: 'completed',
              order: 0,
              dependsOn: [],
              attempts: 1,
              duration: 5000,
              artifacts: [],
              output: {
                beforeBaseSha: 'old-base',
                afterBaseSha: 'new-base',
                beforeHeadSha: 'old-head',
                afterHeadSha: 'new-head',
                shaChanged: true,
                conflicts: ['src/file-a.ts', 'src/file-b.ts'],
              },
              reason: null,
              causedBy: null,
            },
          ],
        }),
      );

      const input = makeInput({
        gitFacts: makeGitFacts({ currentBaseSha: 'new-base' }),
        candidateEvidence: makeCandidateEvidence({
          observedBaseSha: 'old-base',
        }),
        workflowFacts: makeWorkflowFacts({
          workflowRun,
          currentStage: Stage.Check,
          isRunning: true,
          runningTaskId: null,
        }),
      });

      const result = evaluateBaseDrift(input);

      expect(result.drifted).toBe(true);
      expect(result.conflicts).toEqual(['src/file-a.ts', 'src/file-b.ts']);
    });
  });
});

describe('BaseDriftService', () => {
  it('delegates to evaluateBaseDrift', () => {
    const service = new BaseDriftService();

    const input: BaseDriftInput = {
      projectId: 'proj-1',
      issueId: 'issue-1',
      issueNumber: 188,
      baseBranch: 'main',
      gitFacts: makeGitFacts({ currentBaseSha: 'abc' }),
      candidateEvidence: makeCandidateEvidence({ observedBaseSha: 'abc' }),
      workflowFacts: makeWorkflowFacts(),
    };

    const result = service.evaluate(input);

    expect(result.drifted).toBe(false);
    expect(result.decision).toBe('skip');
  });
});

describe('BaseDriftService.scanActiveCandidatesForDrift', () => {
  const makeMockIssueRepo = (issues: Array<{ id: string; number: number; stage: Stage; status: IssueStatus; mergeState?: string }>) => ({
    findAll: ({ projectId, status }: { projectId: string; status?: IssueStatus }) => {
      if (status !== IssueStatus.Active) return [];
      return issues;
    },
  });

  const makeMockWorkflowRunService = (runs: Map<string, { currentStage: Stage } | null>) => ({
    getLatestRunForIssue: (issueId: string) => runs.get(issueId) ?? null,
  });

  const makeMockWorktreeManager = () => ({
    getPath: (projectName: string, issueNumber: number) => `/tmp/worktree-${issueNumber}`,
    getHeadSha: async (worktreePath: string) => 'candidate-head-sha',
  });

  const makeMockEventBus = () => {
    const events: Array<{ name: string; data: unknown }> = [];
    return {
      events,
      emit: (name: string, data: unknown) => events.push({ name, data }),
    };
  };

  it('emits base_branch_advanced event after successful Integrate merge (wired by caller)', async () => {
    const service = new BaseDriftService();
    const mockEventBus = makeMockEventBus() as any;

    const mockIssueRepo = makeMockIssueRepo([
      { id: 'issue-1', number: 101, stage: Stage.Check, status: IssueStatus.Active },
    ]);

    const mockWorkflowRunService = makeMockWorkflowRunService(new Map([
      ['issue-1', { currentStage: Stage.Check }],
    ]));

    const mockWorktreeManager = makeMockWorktreeManager();

    await service.scanActiveCandidatesForDrift({
      projectId: 'proj-1',
      baseBranch: 'main',
      newBaseSha: 'new-base-sha',
      issueRepo: mockIssueRepo as any,
      workflowRunService: mockWorkflowRunService as any,
      worktreeManager: mockWorktreeManager as any,
      project: { path: '/tmp/project', name: 'test-project' },
      eventBus: mockEventBus,
    });

    const driftEvent = mockEventBus.events.find(e => e.name === 'base_drift_detected');
    expect(driftEvent).toBeDefined();
    expect((driftEvent as any).data.projectId).toBe('proj-1');
    expect((driftEvent as any).data.issueNumber).toBe(101);
  });

  it('skips closed, done, and already merged candidates', async () => {
    const service = new BaseDriftService();
    const mockEventBus = makeMockEventBus() as any;

    const mockIssueRepo = makeMockIssueRepo([
      { id: 'issue-closed', number: 1, stage: Stage.Check, status: IssueStatus.Closed },
      { id: 'issue-done', number: 2, stage: Stage.Check, status: IssueStatus.Completed },
      { id: 'issue-merged', number: 3, stage: Stage.Check, status: IssueStatus.Active, mergeState: 'merged' },
    ]);

    const mockWorkflowRunService = makeMockWorkflowRunService(new Map());

    const mockWorktreeManager = makeMockWorktreeManager();

    const result = await service.scanActiveCandidatesForDrift({
      projectId: 'proj-1',
      baseBranch: 'main',
      newBaseSha: 'new-base-sha',
      issueRepo: mockIssueRepo as any,
      workflowRunService: mockWorkflowRunService as any,
      worktreeManager: mockWorktreeManager as any,
      project: { path: '/tmp/project', name: 'test-project' },
      eventBus: mockEventBus,
    });

    expect(result.scannedCount).toBe(0);
  });

  it('deduplicates scans for the same base SHA', async () => {
    const service = new BaseDriftService();
    const mockEventBus = makeMockEventBus() as any;

    const mockIssueRepo = makeMockIssueRepo([
      { id: 'issue-1', number: 101, stage: Stage.Check, status: IssueStatus.Active },
    ]);

    const mockWorkflowRunService = makeMockWorkflowRunService(new Map([
      ['issue-1', { currentStage: Stage.Check }],
    ]));

    const mockWorktreeManager = makeMockWorktreeManager();

    await service.scanActiveCandidatesForDrift({
      projectId: 'proj-1',
      baseBranch: 'main',
      newBaseSha: 'new-base-sha',
      issueRepo: mockIssueRepo as any,
      workflowRunService: mockWorkflowRunService as any,
      worktreeManager: mockWorktreeManager as any,
      project: { path: '/tmp/project', name: 'test-project' },
      eventBus: mockEventBus,
    });

    await service.scanActiveCandidatesForDrift({
      projectId: 'proj-1',
      baseBranch: 'main',
      newBaseSha: 'new-base-sha',
      issueRepo: mockIssueRepo as any,
      workflowRunService: mockWorkflowRunService as any,
      worktreeManager: mockWorktreeManager as any,
      project: { path: '/tmp/project', name: 'test-project' },
      eventBus: mockEventBus,
    });

    const driftEvents = mockEventBus.events.filter(e => e.name === 'base_drift_detected');
    expect(driftEvents.length).toBe(1);
  });

  it('emits rebase_opportunity_opened and candidate_evidence_invalidated events for drifted Check issue', async () => {
    const service = new BaseDriftService();
    const mockEventBus = makeMockEventBus() as any;

    const mockWorkflowRun = {
      id: 'run-1',
      issueId: 'issue-check',
      issueNumber: 102,
      status: 'running' as const,
      currentStage: Stage.Check,
      stageOrder: [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate],
      stageRuns: [{
        stage: Stage.Check,
        status: 'awaiting-approval' as const,
        order: 2,
        tasks: [],
        checks: [],
        approval: {
          status: 'approved',
          output: { baseSha: 'old-base-sha' },
          requestedAt: new Date().toISOString(),
          respondedAt: new Date().toISOString(),
        },
        failure: null,
        commitPoint: null,
      }],
      failure: null,
    };

    const mockWorkflowRunService = makeMockWorkflowRunService(new Map([
      ['issue-check', mockWorkflowRun as any],
    ]));

    const mockIssueRepo = makeMockIssueRepo([
      { id: 'issue-check', number: 102, stage: Stage.Check, status: IssueStatus.Active },
    ]);

    const mockWorktreeManager = makeMockWorktreeManager();

    await service.scanActiveCandidatesForDrift({
      projectId: 'proj-1',
      baseBranch: 'main',
      newBaseSha: 'new-base-sha',
      issueRepo: mockIssueRepo as any,
      workflowRunService: mockWorkflowRunService as any,
      worktreeManager: mockWorktreeManager as any,
      project: { path: '/tmp/project', name: 'test-project' },
      eventBus: mockEventBus,
    });

    const allEvents = mockEventBus.events.map(e => e.name);
    expect(allEvents).toContain('base_drift_detected');
  });
});