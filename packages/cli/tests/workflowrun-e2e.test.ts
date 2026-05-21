import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { DatabaseManager } from '../src/db/database';
import { CoderSessionRepo } from '../src/db/coder-session-repo';
import { initializeDatabase } from '../src/db/migrations';
import { IssueRepo } from '../src/db/issue-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { WorkflowRunRepo } from '../src/db/workflow-run-repo';
import { CheckSuiteRepo } from '../src/db/check-suite-repo';
import { WorkflowApplicationService } from '../src/services/workflow-application-service';
import { WorkflowRunProjection } from '../src/services/workflow-run-projection';
import { StageStateService } from '../src/services/stage-state-service';
import { WorkflowRunService } from '../src/services/workflow-run-service';
import { IssueStatus, MergeState, Stage } from '../src/types';
import { createWorkflowDefinitionSnapshot, WorkflowRun } from '../src/workflow/model';
import { DEFAULT_STAGE_DEFINITIONS } from '../src/workflow/builtins/workflows/mohist-default';

describe('WorkflowRun aggregate end-to-end regressions', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let workflowRunRepo: WorkflowRunRepo;
  let coderSessionRepo: CoderSessionRepo;
  let workflowApplicationService: WorkflowApplicationService;
  let workflowRunService: WorkflowRunService;
  let stageStateService: StageStateService;
  let issueId: string;
  let issueNumber: number;
  let tempDirs: string[];

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    issueRepo = new IssueRepo(db);
    workflowRunRepo = new WorkflowRunRepo(db);
    coderSessionRepo = new CoderSessionRepo(db);
    workflowApplicationService = new WorkflowApplicationService(db);
    workflowRunService = new WorkflowRunService(db);
    stageStateService = new StageStateService(db);
    tempDirs = [];

    const project = new ProjectRepo(db).create({ name: 'WorkflowRun E2E', path: '/tmp/workflowrun-e2e' });
    const issue = issueRepo.create({ number: 188, projectId: project.id, title: 'Aggregate workflow truth' });
    issueId = issue.id;
    issueNumber = issue.number;
  });

  afterEach(() => {
    db.close();
    for (const dir of tempDirs) fs.rmSync(dir, { recursive: true, force: true });
  });

  function startWorkflow(): void {
    workflowApplicationService.startWorkflow({ issueId, issueNumber, startedBy: 'test' });
  }

  function completePlanThroughApprovalRequest(): void {
    for (const taskId of ['proposal', 'specs', 'design', 'tasks', 'self-review']) {
      workflowApplicationService.completeTask({ issueId, stage: Stage.Plan, taskId, result: { status: 'completed' } });
    }
    for (const checkName of ['proposal-complete', 'specs-complete', 'design-complete', 'tasks-valid', 'self-review-passed', 'health:plan']) {
      workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Plan, result: { name: checkName, status: 'pass' } });
    }
  }

  function advanceToBuild(): void {
    completePlanThroughApprovalRequest();
    workflowApplicationService.approveStage({ issueId, stage: Stage.Plan, approval: { output: { approved: true } } });
  }

  function completeBuild(tasks = [{ id: 'T-001', title: 'Build aggregate', order: 0 }]): void {
    workflowApplicationService.materializeTasks({ issueId, stage: Stage.Build, tasks });
    for (const task of tasks) {
      workflowApplicationService.completeTask({ issueId, stage: Stage.Build, taskId: task.id, result: { status: 'completed' } });
    }
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
  }

  function mergeReadyOutput(candidateHeadSha: string): Record<string, unknown> {
    return {
      kind: 'merge-ready',
      targetBranch: 'master',
      strategy: 'squash',
      baseSha: 'base-sha',
      candidateHeadSha,
      mergeBaseSha: 'base-sha',
      canMerge: true,
      conflictFiles: [],
      checkedAt: '2026-05-15T00:00:00.000Z',
    };
  }

  function advanceToIntegrate(): void {
    advanceToBuild();
    completeBuild();
    workflowApplicationService.completeTask({ issueId, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Check, result: { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } } });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Check, result: { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-check') } });
    workflowApplicationService.approveStage({ issueId, stage: Stage.Check, approval: { output: { approved: true } } });
  }

  function applyProjection(run: WorkflowRun): void {
    new WorkflowRunProjection(db).apply({
      run,
      decision: { events: [], nextWork: { kind: 'complete' } },
    });
  }

  function forgeCompletedDefaultStagesBeforeIntegrate(run: WorkflowRun): void {
    for (const task of run.stageRun(Stage.Plan).tasks) task.status = 'completed';
    for (const check of run.stageRun(Stage.Plan).checks) check.status = 'passed';
    run.stageRun(Stage.Plan).approval = {
      status: 'approved',
      output: { approved: true },
      requestedAt: '2026-05-18T00:00:00.000Z',
      respondedAt: '2026-05-18T00:01:00.000Z',
    };
    for (const task of run.stageRun(Stage.Build).tasks) task.status = 'completed';
    for (const check of run.stageRun(Stage.Build).checks) check.status = 'passed';
    for (const task of run.stageRun(Stage.Check).tasks) task.status = 'completed';
    for (const check of run.stageRun(Stage.Check).checks) {
      check.status = 'passed';
      if (check.name === 'review-passed') check.output = { verdict: 'PASS', snapshotSha: 'head' };
      if (check.name === 'merge-ready') check.output = { candidateHeadSha: 'head' };
    }
    run.stageRun(Stage.Check).approval = {
      status: 'approved',
      output: { approved: true },
      requestedAt: '2026-05-18T00:02:00.000Z',
      respondedAt: '2026-05-18T00:03:00.000Z',
    };
  }

  it('advances Plan to Done by WorkflowRun stageOrder and projects issue stage/status', () => {
    startWorkflow();
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Plan, status: IssueStatus.Active });

    advanceToBuild();
    expect(workflowRunRepo.loadActiveAggregate(issueId)?.snapshot()).toMatchObject({
      currentStage: Stage.Build,
      stageOrder: [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate],
    });
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Build, status: IssueStatus.Active });

    issueRepo.updateStage(issueId, Stage.Done);
    completeBuild([
      { id: 'T-001', title: 'Build aggregate', order: 0 },
      { id: 'T-002', title: 'Expose projection', order: 1 },
    ]);
    expect(workflowRunRepo.loadActiveAggregate(issueId)?.snapshot().currentStage).toBe(Stage.Check);
    expect(issueRepo.findById(issueId)?.stage).toBe(Stage.Check);

    workflowApplicationService.completeTask({ issueId, stage: Stage.Check, taskId: 'ai-review', result: { status: 'completed' } });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Check, result: { name: 'health:check', status: 'pass' } });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Check, result: { name: 'review-passed', status: 'pass', output: { verdict: 'PASS', snapshotSha: 'sha-check' } } });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Check, result: { name: 'merge-ready', status: 'pass', output: mergeReadyOutput('sha-check') } });
    workflowApplicationService.approveStage({ issueId, stage: Stage.Check, approval: { output: { approved: true } } });
    workflowApplicationService.completeTask({ issueId, stage: Stage.Integrate, taskId: 'integrate:spec-sync', result: { status: 'completed', output: { synced: ['workflow-run'] } } });
    workflowApplicationService.completeTask({ issueId, stage: Stage.Integrate, taskId: 'integrate:archive-change', result: { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } } });
    workflowApplicationService.completeTask({
      issueId,
      stage: Stage.Integrate,
      taskId: 'integrate:merge',
      result: {
        status: 'completed',
        output: { targetBranch: 'main', baseSha: 'base', candidateHeadSha: 'head', landedSha: 'landed', rebased: false },
      },
    });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Integrate, result: { name: 'health:integrate', status: 'pass' } });

    const latest = workflowRunService.getLatestRunForIssue(issueId)!;
    expect(latest.status).toBe('passed');
    expect(latest.stageRuns.map(stageRun => stageRun.stage)).toEqual([Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate]);
    expect(latest.stageRuns.every(stageRun => stageRun.status === 'passed')).toBe(true);
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Done, status: IssueStatus.Completed });
  });

  it('projects Done from later workflow evidence despite a stale failed coder session', () => {
    const staleSession = coderSessionRepo.insert({
      issueId,
      acpSessionId: 'acp-stale-failed',
      stage: Stage.Check,
      taskDescription: 'Older failed review attempt',
    });
    coderSessionRepo.markFailed(staleSession.id, 'review crashed');

    startWorkflow();
    advanceToIntegrate();
    workflowApplicationService.completeTask({ issueId, stage: Stage.Integrate, taskId: 'integrate:spec-sync', result: { status: 'completed', output: { synced: ['workflow-run'] } } });
    workflowApplicationService.completeTask({ issueId, stage: Stage.Integrate, taskId: 'integrate:archive-change', result: { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } } });
    workflowApplicationService.completeTask({
      issueId,
      stage: Stage.Integrate,
      taskId: 'integrate:merge',
      result: {
        status: 'completed',
        output: { targetBranch: 'main', baseSha: 'base', candidateHeadSha: 'head', landedSha: 'landed', rebased: false },
      },
    });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Integrate, result: { name: 'health:integrate', status: 'pass' } });

    const latest = workflowRunService.getLatestRunForIssue(issueId)!;
    expect(latest.status).toBe('passed');
    expect(coderSessionRepo.findById(staleSession.id)).toMatchObject({ status: 'failed', failureReason: 'review crashed' });
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Done, status: IssueStatus.Completed });
    expect(issueRepo.findById(issueId)?.blockedReason ?? null).toBeNull();
  });

  it('projects interrupted recovery visibly instead of active issue status', () => {
    startWorkflow();
    workflowApplicationService.startTaskAttempt({
      issueId,
      stage: Stage.Plan,
      taskId: 'proposal',
      evidence: { executionId: 'plan-proposal-lost' },
    });
    workflowApplicationService.interruptRunningWorkAttempts({
      issueId,
      reason: 'agent-lost',
      diagnostic: 'agent process disappeared',
    });

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Plan,
      status: IssueStatus.Interrupted,
      blockedReason: 'agent process disappeared',
    });
    expect(workflowApplicationService.getRecoveryProjection(issueId)).toMatchObject({
      latestAttemptState: 'interrupted',
      workflowSummaryState: 'waiting-for-recovery',
      allowedActions: expect.arrayContaining(['resume', 'rerun', 'inspect']),
    });
  });

  it('rejects a passed projection that did not reach the workflow terminal stage', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_impossible_check', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    run.status = 'passed';
    run.currentStage = Stage.Check;
    for (const stageRun of run.stageRuns) {
      if (stageRun.stage === Stage.Plan || stageRun.stage === Stage.Build || stageRun.stage === Stage.Check) {
        stageRun.status = 'passed';
      }
    }

    applyProjection(run);

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Check,
      status: IssueStatus.Blocked,
      blockedReason: expect.stringContaining('current stage is check, expected terminal stage integrate'),
    });
  });

  it('projects a custom workflow as completed without an Integrate stage or merge delivery', () => {
    const snapshot = createWorkflowDefinitionSnapshot({
      definition: {
        id: 'project/no-local-merge',
        stages: [
          {
            stage: Stage.Plan,
            tasks: [{ id: 'design', title: 'Design', uses: 'mohist/agent' }],
            checks: [{ name: 'design-file', title: 'Design file', uses: 'mohist/artifact-exists' }],
          },
          {
            stage: Stage.Build,
            tasks: [{ id: 'implement', title: 'Implement', uses: 'mohist/agent' }],
            checks: [{ name: 'tests', title: 'Tests', uses: 'mohist/shell' }],
          },
        ],
      },
      source: { type: 'project', path: '.mohist/workflows/no-local-merge.yaml' },
      capturedAt: '2026-05-19T00:00:00.000Z',
    });
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_custom_done', issueId, issueNumber, workflowDefinitionSnapshot: snapshot });

    run.completeTask(Stage.Plan, 'design', { status: 'completed' });
    run.recordCheckResult(Stage.Plan, { name: 'design-file', status: 'pass' });
    run.completeTask(Stage.Build, 'implement', { status: 'completed' });
    run.recordCheckResult(Stage.Build, { name: 'tests', status: 'pass' });
    applyProjection(run);

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
      mergeState: undefined,
      blockedReason: undefined,
    });
  });

  it('projects frozen delivery metadata from a custom locking check', () => {
    const snapshot = createWorkflowDefinitionSnapshot({
      definition: {
        id: 'project/remote-merge',
        stages: [
          {
            stage: Stage.Check,
            tasks: [],
            checks: [
              { name: 'pr-merged', title: 'PR merged', uses: 'mohist/pr-merged' },
              { name: 'delivery-health', title: 'Delivery health', uses: 'mohist/health-gate' },
            ],
            checkPolicies: [
              { checkName: 'pr-merged', phase: 'post-task' },
              { checkName: 'delivery-health', phase: 'post-task' },
            ],
          },
        ],
      },
      source: { type: 'project', path: '.mohist/workflows/remote-merge.yaml' },
      capturedAt: '2026-05-19T00:00:00.000Z',
    });
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_remote_merge_done', issueId, issueNumber, workflowDefinitionSnapshot: snapshot });

    run.recordCheckResult(Stage.Check, { name: 'pr-merged', status: 'pass', output: { mergedSha: 'remote-landed' } });
    run.recordCheckResult(Stage.Check, { name: 'delivery-health', status: 'pass' });

    const check = stageStateService.getIssueStageStateFromWorkflowRun(run).find(stage => stage.stage === Stage.Check)!;
    expect(check.deliveryMetadata?.remoteMerge).toMatchObject({ status: 'passed', mergedSha: 'remote-landed' });
    expect(check.deliveryMetadata?.frozen).toBe(true);
  });

  it('projects check suite checks from stage checks without a fixed Check stage', () => {
    const snapshot = createWorkflowDefinitionSnapshot({
      definition: {
        id: 'project/plan-review-suite',
        stages: DEFAULT_STAGE_DEFINITIONS.map(definition => definition.stage === Stage.Plan
          ? {
            stage: Stage.Plan,
            tasks: [{ id: 'plan-review', title: 'Plan review', uses: 'mohist/agent' }],
            checks: [
              {
                name: 'verify-plan',
                title: 'Verify plan',
                uses: 'mohist/health-gate',
              },
              {
                name: 'plan-verdict',
                title: 'Plan verdict',
                uses: 'mohist/verdict',
              },
              {
                name: 'plan-candidate',
                title: 'Plan candidate',
                uses: 'mohist/merge-ready',
              },
            ],
            requiresApproval: true,
          }
          : definition),
      },
      source: { type: 'project', path: '.mohist/workflows/plan-review-suite.yaml' },
      capturedAt: '2026-05-19T00:00:00.000Z',
    });
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_plan_review_suite', issueId, issueNumber, workflowDefinitionSnapshot: snapshot });
    const suite = new CheckSuiteRepo(db).create({ issueId, snapshotSha: 'old-sha' });

    run.completeTask(Stage.Plan, 'plan-review', { status: 'completed' });
    run.recordCheckResult(Stage.Plan, { name: 'verify-plan', status: 'pass', output: { headSha: 'plan-sha' } });
    run.recordCheckResult(Stage.Plan, { name: 'plan-verdict', status: 'pass', output: { verdict: 'PASS', reviewedSha: 'plan-sha' } });
    run.recordCheckResult(Stage.Plan, { name: 'plan-candidate', status: 'pass', output: { headSha: 'plan-sha' } });
    applyProjection(run);

    const projected = new CheckSuiteRepo(db).findById(suite.id)!;
    expect(projected.status).toBe('awaiting-approval');
    expect(projected.checks).toMatchObject({
      'verify-plan': { status: 'passed', output: { headSha: 'plan-sha' } },
      'plan-verdict': { status: 'passed', output: { verdict: 'PASS', reviewedSha: 'plan-sha' } },
      'plan-candidate': { status: 'passed', output: { headSha: 'plan-sha' } },
    });
  });

  it('rejects mergeState-only Done projection without terminal stage task/check completion', () => {
    issueRepo.update(issueId, { mergeState: MergeState.Merged });
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_merge_only_done', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    run.status = 'passed';
    run.currentStage = Stage.Integrate;
    for (const stageRun of run.stageRuns) {
      stageRun.status = 'passed';
    }

    applyProjection(run);

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Integrate,
      status: IssueStatus.Blocked,
      mergeState: MergeState.Merged,
      blockedReason: expect.stringContaining('proposal task is pending'),
    });
  });

  it('rejects a passed projection when an earlier declared stage was not completed', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_incomplete_earlier_stage', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    run.status = 'passed';
    run.currentStage = Stage.Integrate;
    for (const stageRun of run.stageRuns) {
      stageRun.status = 'passed';
    }

    const integrate = run.stageRun(Stage.Integrate);
    const specSync = integrate.tasks.find(task => task.id === 'integrate:spec-sync')!;
    specSync.status = 'completed';
    const archive = integrate.tasks.find(task => task.id === 'integrate:archive-change')!;
    archive.status = 'completed';
    archive.output = { archivePath: 'openspec/changes/archive/188-workflowrun' };
    const merge = integrate.tasks.find(task => task.id === 'integrate:merge')!;
    merge.status = 'completed';
    merge.output = { landedSha: 'landed' };
    const health = integrate.checks.find(check => check.name === 'health:integrate')!;
    health.status = 'passed';

    applyProjection(run);

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Integrate,
      status: IssueStatus.Blocked,
      blockedReason: expect.stringContaining('proposal task is pending'),
    });
  });

  it('projects Done with service-call wrapped archive and merge delivery evidence', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_service_call_done', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    run.status = 'passed';
    run.currentStage = Stage.Integrate;
    for (const stageRun of run.stageRuns) {
      stageRun.status = 'passed';
    }
    forgeCompletedDefaultStagesBeforeIntegrate(run);

    const integrate = run.stageRun(Stage.Integrate);
    const specSync = integrate.tasks.find(task => task.id === 'integrate:spec-sync')!;
    specSync.status = 'completed';
    specSync.output = { kind: 'service-call-task', result: { synced: ['workflow-run'] } };
    const archive = integrate.tasks.find(task => task.id === 'integrate:archive-change')!;
    archive.status = 'completed';
    archive.output = { kind: 'service-call-task', result: { archivePath: null, success: true } };
    const merge = integrate.tasks.find(task => task.id === 'integrate:merge')!;
    merge.status = 'completed';
    merge.output = {
      kind: 'service-call-task',
      result: { targetBranch: 'master', baseSha: 'base', candidateHeadSha: 'head', landedSha: 'landed' },
    };
    integrate.commitPoint = {
      taskId: 'integrate:merge',
      uses: 'mohist/merge',
      metadata: { targetBranch: 'master', baseSha: 'base', candidateHeadSha: 'head', landedSha: 'landed' },
      createdAt: '2026-05-18T00:00:00.000Z',
    };
    const health = integrate.checks.find(check => check.name === 'health:integrate')!;
    health.status = 'passed';

    applyProjection(run);

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
      blockedReason: undefined,
    });
  });

  it('rejects targetBranch-only Integrate delivery when projecting Done', () => {
    const { run } = WorkflowRun.startWorkflow({ id: 'wr_target_branch_only_done', issueId, issueNumber, definitions: DEFAULT_STAGE_DEFINITIONS });
    run.status = 'passed';
    run.currentStage = Stage.Integrate;
    for (const stageRun of run.stageRuns) {
      stageRun.status = 'passed';
    }
    forgeCompletedDefaultStagesBeforeIntegrate(run);

    const integrate = run.stageRun(Stage.Integrate);
    const specSync = integrate.tasks.find(task => task.id === 'integrate:spec-sync')!;
    specSync.status = 'completed';
    specSync.output = { synced: ['workflow-run'] };
    const archive = integrate.tasks.find(task => task.id === 'integrate:archive-change')!;
    archive.status = 'completed';
    archive.output = { archivePath: 'openspec/changes/archive/188-workflowrun' };
    const merge = integrate.tasks.find(task => task.id === 'integrate:merge')!;
    merge.status = 'completed';
    merge.output = { targetBranch: 'main', baseSha: 'base', candidateHeadSha: 'head' };
    integrate.commitPoint = {
      taskId: 'integrate:merge',
      uses: 'mohist/merge',
      metadata: { targetBranch: 'main', baseSha: 'base', candidateHeadSha: 'head' },
      createdAt: '2026-05-18T00:00:00.000Z',
    };
    const health = integrate.checks.find(check => check.name === 'health:integrate')!;
    health.status = 'passed';

    applyProjection(run);

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Integrate,
      status: IssueStatus.Blocked,
      blockedReason: expect.stringContaining('integrate:merge landedSha evidence is missing'),
    });
  });

  it('fails a task with task-failed and does not run later tasks or checks', () => {
    startWorkflow();

    const { decision } = workflowApplicationService.completeTask({
      issueId,
      stage: Stage.Plan,
      taskId: 'proposal',
      result: { status: 'failed', reason: 'agent crashed' },
    });

    const latest = workflowRunService.getLatestRunForIssue(issueId)!;
    const plan = latest.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)!;

    expect(latest.status).toBe('failed');
    expect(decision.nextWork).toMatchObject({ kind: 'failed', reason: { reason: 'task-failed', taskId: 'proposal' } });
    expect(plan.status).toBe('failed');
    expect(plan.tasks.find(task => task.taskId === 'proposal')).toMatchObject({ status: 'failed', reason: 'agent crashed' });
    expect(plan.tasks.find(task => task.taskId === 'specs')?.status).toBe('pending');
    expect(plan.checks.every(check => check.status === 'pending' && check.runCount === 0)).toBe(true);
    expect(latest.stageRuns.find(stageRun => stageRun.stage === Stage.Build)?.status).toBe('pending');
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Plan, status: IssueStatus.Blocked });
  });

  it('retries the failed current stage without creating a new Plan WorkflowRun', () => {
    startWorkflow();
    advanceToBuild();
    completeBuild();
    workflowApplicationService.completeTask({
      issueId,
      stage: Stage.Check,
      taskId: 'ai-review',
      result: { status: 'failed', reason: 'review session cancelled' },
    });

    const failedRun = workflowRunService.getLatestRunForIssue(issueId)!;
    expect(failedRun.status).toBe('failed');
    expect(failedRun.currentStage).toBe(Stage.Check);
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Check, status: IssueStatus.Blocked });

    const retry = workflowApplicationService.retryStage({ issueId, stage: Stage.Check });
    expect(retry.decision.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review' });

    const retriedRun = workflowRunService.getActiveRunForIssue(issueId)!;
    expect(retriedRun.id).toBe(failedRun.id);
    expect(retriedRun.status).toBe('running');
    expect(retriedRun.currentStage).toBe(Stage.Check);
    expect(retriedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)?.status).toBe('passed');
    expect(retriedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Build)?.status).toBe('passed');
    expect(retriedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)?.tasks.every(task => task.status === 'completed')).toBe(true);
    expect(retriedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Build)?.tasks.every(task => task.status === 'completed')).toBe(true);
    expect(retriedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Check)).toMatchObject({
      status: 'running',
      approvalStatus: null,
    });
    expect(retriedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Check)?.tasks.find(task => task.taskId === 'ai-review')).toMatchObject({
      status: 'pending',
      reason: null,
    });
    expect(workflowRunRepo.findByIssueId(issueId)).toHaveLength(1);
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Check, status: IssueStatus.Active });
  });

  it('records repair task causedBy metadata and reruns checks by aggregate decision', () => {
    startWorkflow();
    advanceToBuild();
    workflowApplicationService.materializeTasks({ issueId, stage: Stage.Build, tasks: [{ id: 'T-001', title: 'Build aggregate', order: 0 }] });
    workflowApplicationService.completeTask({ issueId, stage: Stage.Build, taskId: 'T-001', result: { status: 'completed' } });

    const firstFailure = workflowApplicationService.recordCheckResult({
      issueId,
      stage: Stage.Build,
      result: { name: 'health:build', status: 'fail', message: 'typecheck failed' },
    });

    expect(firstFailure.decision.nextWork).toEqual({ kind: 'task', stage: Stage.Build, taskId: 'fix-build-health' });
    let build = workflowRunService.getActiveRunForIssue(issueId)!.stageRuns.find(stageRun => stageRun.stage === Stage.Build)!;
    expect(build.tasks.find(task => task.taskId === 'fix-build-health')).toMatchObject({
      status: 'pending',
      reason: 'typecheck failed',
      causedByType: 'check-failure',
      causedByCheckName: 'health:build',
    });

    const fix = workflowApplicationService.completeTask({ issueId, stage: Stage.Build, taskId: 'fix-build-health', result: { status: 'completed' } });
    expect(fix.decision.nextWork).toEqual({ kind: 'check', stage: Stage.Build, checkName: 'health:build' });

    const recheck = workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Build, result: { name: 'health:build', status: 'pass' } });
    expect(recheck.decision.nextWork).toEqual({ kind: 'task', stage: Stage.Check, taskId: 'ai-review' });
    build = workflowRunService.getActiveRunForIssue(issueId)!.stageRuns.find(stageRun => stageRun.stage === Stage.Build)!;
    expect(build.status).toBe('passed');
    expect(build.checks.find(check => check.checkName === 'health:build')).toMatchObject({ status: 'passed', runCount: 2 });
  });

  it('keeps approval await, approve, reject, and resume projections consistent', () => {
    startWorkflow();
    completePlanThroughApprovalRequest();

    expect(workflowApplicationService.resumeDecision(issueId).nextWork).toEqual({ kind: 'await-approval', stage: Stage.Plan });
    expect(workflowRunService.getActiveRunForIssue(issueId)!.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)).toMatchObject({
      status: 'awaiting-approval',
      approvalStatus: 'awaiting',
    });
    expect(issueRepo.findById(issueId)?.approvalState).toMatchObject({ stage: Stage.Plan, status: 'awaiting' });
    expect(stageStateService.getIssueStageState(issueId).find(stage => stage.stage === Stage.Plan)).toMatchObject({
      status: 'awaiting-approval',
      approval: { status: 'awaiting' },
    });

    workflowApplicationService.approveStage({ issueId, stage: Stage.Plan, approval: { output: { approver: 'qa' } } });
    expect(workflowApplicationService.resumeDecision(issueId).nextWork).toEqual({
      kind: 'blocked',
      stage: Stage.Build,
      reason: { complete: false, reason: 'dynamic-source-not-evaluated', stage: Stage.Build },
    });
    expect(workflowRunService.getActiveRunForIssue(issueId)!.currentStage).toBe(Stage.Build);
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Build, approvalState: undefined });
    expect(stageStateService.getIssueStageState(issueId).find(stage => stage.stage === Stage.Plan)?.approval).toMatchObject({ status: 'approved' });

    const rejected = issueRepo.create({ number: 189, projectId: issueRepo.findById(issueId)!.projectId, title: 'Reject aggregate approval' });
    issueId = rejected.id;
    issueNumber = rejected.number;
    startWorkflow();
    completePlanThroughApprovalRequest();
    workflowApplicationService.rejectStage({ issueId, stage: Stage.Plan, approval: { output: { reason: 'needs more design' } } });

    const rejectedRun = workflowRunService.getLatestRunForIssue(issueId)!;
    expect(rejectedRun.status).toBe('failed');
    expect(rejectedRun.stageRuns.find(stageRun => stageRun.stage === Stage.Plan)).toMatchObject({
      status: 'failed',
      approvalStatus: 'rejected',
      approvalOutput: { reason: 'needs more design' },
    });
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Plan, status: IssueStatus.Blocked, approvalState: undefined });
    expect(stageStateService.getIssueStageState(issueId).find(stage => stage.stage === Stage.Plan)).toMatchObject({
      status: 'failed',
      approval: { status: 'rejected', output: { reason: 'needs more design' } },
    });
  });

  it('preserves delivery metadata and manual-intervention evidence after post-delivery check failure', () => {
    startWorkflow();
    advanceToIntegrate();

    workflowApplicationService.completeTask({ issueId, stage: Stage.Integrate, taskId: 'integrate:spec-sync', result: { status: 'completed', output: { changedSpecs: ['workflow-run'] } } });
    workflowApplicationService.completeTask({ issueId, stage: Stage.Integrate, taskId: 'integrate:archive-change', result: { status: 'completed', output: { archivePath: 'openspec/changes/archive/188-workflowrun' } } });
    workflowApplicationService.completeTask({
      issueId,
      stage: Stage.Integrate,
      taskId: 'integrate:merge',
      result: {
        status: 'completed',
        output: { targetBranch: 'main', baseSha: 'base123', candidateHeadSha: 'head456', landedSha: 'landed789', rebased: true },
      },
    });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Integrate, result: { name: 'health:integrate', status: 'fail', message: 'post-delivery health failed' } });

    const latest = workflowRunService.getLatestRunForIssue(issueId)!;
    const integrate = latest.stageRuns.find(stageRun => stageRun.stage === Stage.Integrate)!;
    const stageProjection = stageStateService.getIssueStageStateFromWorkflowRun(latest).find(stage => stage.stage === Stage.Integrate)!;

    expect(latest.status).toBe('failed');
    expect(integrate.tasks.find(task => task.taskId === 'integrate:merge')?.output).toMatchObject({ landedSha: 'landed789', targetBranch: 'main' });
    expect(integrate.tasks.some(task => task.taskId === 'fix-integrate-health')).toBe(false);
    expect(stageProjection.failure).toMatchObject({ reason: 'post-commit-check-failed', checkName: 'health:integrate', message: 'post-delivery health failed' });
    expect(stageProjection.deliveryMetadata?.merge).toMatchObject({ landedSha: 'landed789', targetBranch: 'main', rebased: true });
    expect(stageProjection.deliveryMetadata?.frozen).toBe(true);
    expect(issueRepo.findById(issueId)).toMatchObject({ stage: Stage.Integrate, status: IssueStatus.Blocked });

    workflowApplicationService.retryStage({ issueId, stage: Stage.Integrate });
    workflowApplicationService.recordCheckResult({ issueId, stage: Stage.Integrate, result: { name: 'health:integrate', status: 'pass' } });

    expect(issueRepo.findById(issueId)).toMatchObject({
      stage: Stage.Done,
      status: IssueStatus.Completed,
      blockedReason: undefined,
    });
  });

  it('loads partial active WorkflowRun data without importing tasks.json side effects', () => {
    const projectPath = fs.mkdtempSync(path.join(os.tmpdir(), 'workflowrun-read-repair-'));
    tempDirs.push(projectPath);
    const tasksPath = path.join(projectPath, 'tasks.json');
    fs.writeFileSync(tasksPath, JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', title: 'Existing running task', order: 0, passes: true, error: 'ignored legacy progress' },
        { id: 'T-002', title: 'Materialized after repair', order: 1 },
      ],
    }), 'utf-8');

    const now = new Date().toISOString();
    db.run(
      `INSERT INTO workflow_runs (id, issue_id, issue_number, status, current_stage, created_at, updated_at)
       VALUES (?, ?, ?, 'running', 'build', ?, ?)`,
      ['wr_partial_e2e', issueId, issueNumber, now, now],
    );
    for (const [stage, order] of [[Stage.Plan, 0], [Stage.Build, 1], [Stage.Check, 2], [Stage.Integrate, 3]] as const) {
      db.run(
        `INSERT INTO workflow_stage_runs (id, workflow_run_id, stage, status, stage_order, created_at, updated_at)
         VALUES (?, ?, ?, ?, ?, ?, ?)`,
        [`wr_partial_e2e/${stage}`, 'wr_partial_e2e', stage, stage === Stage.Build ? 'running' : 'pending', order, now, now],
      );
    }
    db.run(
      `INSERT INTO workflow_tasks
       (id, workflow_run_id, stage_run_id, task_id, title, status, task_order, attempts, duration, artifacts, output, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, 'running', 0, 1, 120, '[]', ?, ?, ?)`,
      ['wr_partial_e2e/build/T-001', 'wr_partial_e2e', 'wr_partial_e2e/build', 'T-001', 'Existing running task', JSON.stringify({ progress: 'halfway' }), now, now],
    );
    db.run(
      `INSERT INTO workflow_checks
       (id, workflow_run_id, stage_run_id, check_name, title, status, message, output, run_count, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, 'pending', NULL, NULL, 0, ?, ?)`,
      ['wr_partial_e2e/build/health:build', 'wr_partial_e2e', 'wr_partial_e2e/build', 'health:build', 'Build health gate', now, now],
    );

    const loaded = workflowRunRepo.loadActiveAggregate(issueId)!.snapshot();
    const build = loaded.stageRuns.find(stage => stage.stage === Stage.Build)!;
    const latest = workflowRunService.getActiveRunForIssue(issueId)!;
    const stageProjection = stageStateService.getIssueStageStateFromWorkflowRun(latest);

    expect(loaded.stageRuns.find(stage => stage.stage === Stage.Plan)?.tasks.map(task => task.id)).toEqual(['proposal', 'specs', 'design', 'tasks', 'self-review']);
    expect(loaded.stageRuns.find(stage => stage.stage === Stage.Integrate)?.tasks.map(task => task.id)).toEqual(['integrate:spec-sync', 'integrate:archive-change', 'integrate:merge']);
    expect(build.tasks).toEqual([
      expect.objectContaining({ id: 'T-001', status: 'running', output: { progress: 'halfway' } }),
    ]);
    expect(build.checks).toEqual([expect.objectContaining({ name: 'health:build', status: 'pending' })]);
    expect(stageProjection.find(stage => stage.stage === Stage.Build)).toMatchObject({
      status: 'running',
      tasks: [
        expect.objectContaining({ taskId: 'T-001', status: 'running', output: { progress: 'halfway' } }),
      ],
      checks: [expect.objectContaining({ checkName: 'health:build', status: 'pending' })],
    });
  });
});
