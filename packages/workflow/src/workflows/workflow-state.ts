import type {
  CheckStateSnapshot,
  TaskRunSnapshot,
  WorkflowRunStatus as DomainWorkflowRunStatus,
} from '../model';
import { WorkflowRun } from '../model';
import type { WorkflowState, WorkflowStateStatus } from './types';

export function stateFromRun(run: WorkflowRun): WorkflowState {
  const snapshot = run.snapshot();
  return {
    id: snapshot.id,
    status: workflowStateStatusFromDomain(snapshot.status),
    currentStage: snapshot.currentStage,
    stageOrder: [...snapshot.stageOrder],
    definition: snapshot.workflowDefinitionSnapshot,
    stages: snapshot.stageRuns,
    failure: snapshot.failure,
  };
}

export function workflowRunFromState(state: WorkflowState): WorkflowRun {
  const snapshot = {
    id: state.id,
    issueId: state.id,
    issueNumber: 0,
    status: domainStatusFromWorkflowState(state.status),
    currentStage: state.currentStage,
    stageOrder: [...state.stageOrder],
    workflowDefinitionSnapshot: state.definition,
    stageRuns: state.stages,
    failure: state.failure,
  };
  const { run } = WorkflowRun.startWorkflow({
    id: snapshot.id,
    issueId: snapshot.issueId,
    issueNumber: snapshot.issueNumber,
    workflowDefinitionSnapshot: snapshot.workflowDefinitionSnapshot,
  });
  restoreRunFields(run, snapshot);
  return run;
}

function workflowStateStatusFromDomain(status: DomainWorkflowRunStatus): WorkflowStateStatus {
  if (status === 'passed') return 'completed';
  return status;
}

function domainStatusFromWorkflowState(status: WorkflowStateStatus): DomainWorkflowRunStatus {
  if (status === 'completed') return 'passed';
  return status;
}

function restoreRunFields(run: WorkflowRun, snapshot: ReturnType<WorkflowRun['snapshot']>): void {
  run.status = snapshot.status;
  run.currentStage = snapshot.currentStage;
  run.failure = snapshot.failure;

  for (const stageSnapshot of snapshot.stageRuns) {
    const stageRun = run.stageRun(stageSnapshot.stage);
    stageRun.status = stageSnapshot.status;
    stageRun.attemptSequence = stageSnapshot.attemptSequence ?? stageRun.attemptSequence;
    stageRun.approval = stageSnapshot.approval ? { ...stageSnapshot.approval } : null;
    stageRun.failure = stageSnapshot.failure;
    stageRun.commitPoint = stageSnapshot.commitPoint;
    stageRun.workSourceState = stageSnapshot.workSourceState ?? { evaluated: false };
    restoreTasks(stageRun, stageSnapshot.tasks);
    restoreChecks(stageRun, stageSnapshot.checks);
  }
}

function restoreTasks(stageRun: ReturnType<WorkflowRun['currentStageRun']>, tasks: TaskRunSnapshot[]): void {
  stageRun.tasks.splice(0, stageRun.tasks.length);
  for (const taskSnapshot of tasks) {
    const task = stageRun.materializeTaskForPersistence(
      taskSnapshot.id,
      taskSnapshot.title,
      taskSnapshot.order,
      taskSnapshot.uses,
    );
    task.status = taskSnapshot.status;
    task.dependsOn = [...taskSnapshot.dependsOn];
    task.attempts = taskSnapshot.attempts;
    task.duration = taskSnapshot.duration;
    task.artifacts = [...taskSnapshot.artifacts];
    task.events = [...taskSnapshot.events];
    task.output = taskSnapshot.output;
    task.reason = taskSnapshot.reason;
    task.causedBy = taskSnapshot.causedBy;
    task.resetBy = taskSnapshot.resetBy;
    task.latestAttempt = taskSnapshot.latestAttempt;
  }
}

function restoreChecks(stageRun: ReturnType<WorkflowRun['currentStageRun']>, checks: CheckStateSnapshot[]): void {
  stageRun.checks.splice(0, stageRun.checks.length);
  for (const checkSnapshot of checks) {
    const check = stageRun.materializeCheckForPersistence(checkSnapshot.name, checkSnapshot.title);
    check.status = checkSnapshot.status;
    check.message = checkSnapshot.message;
    check.output = checkSnapshot.output;
    check.runCount = checkSnapshot.runCount;
    check.latestAttempt = checkSnapshot.latestAttempt;
  }
}
