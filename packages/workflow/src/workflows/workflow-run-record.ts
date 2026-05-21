import type {
  CheckRunState,
  TaskRunState,
  WorkflowRunState,
} from '../model';
import { WorkflowRun } from '../model';
import type { WorkflowRunRecord } from './types';

export function recordFromRun(run: WorkflowRun): WorkflowRunRecord {
  const state = run.state();
  return {
    id: state.id,
    definition: run.definition,
    run: {
      status: statusFromDomain(state.status),
      currentStage: state.currentStage,
      stageOrder: [...state.stageOrder],
      stages: state.stageRuns,
      failure: state.failure,
    },
  };
}

export function runFromRecord(record: WorkflowRunRecord): WorkflowRun {
  const state: WorkflowRunState = {
    id: record.id,
    issueId: record.id,
    issueNumber: 0,
    status: statusToDomain(record.run.status),
    currentStage: record.run.currentStage,
    stageOrder: [...record.run.stageOrder],
    stageRuns: record.run.stages,
    failure: record.run.failure,
  };
  const { run } = WorkflowRun.startWorkflow({
    id: state.id,
    issueId: state.issueId,
    issueNumber: state.issueNumber,
    definition: record.definition,
  });
  restoreRunFields(run, state);
  return run;
}

function statusFromDomain(status: WorkflowRunState['status']): WorkflowRunRecord['run']['status'] {
  if (status === 'passed') return 'completed';
  return status;
}

function statusToDomain(status: WorkflowRunRecord['run']['status']): WorkflowRunState['status'] {
  if (status === 'completed') return 'passed';
  return status;
}

function restoreRunFields(run: WorkflowRun, state: WorkflowRunState): void {
  run.status = state.status;
  run.currentStage = state.currentStage;
  run.failure = state.failure;

  for (const stageState of state.stageRuns) {
    const stageRun = run.stageRun(stageState.stage);
    stageRun.status = stageState.status;
    stageRun.attemptSequence = stageState.attemptSequence ?? stageRun.attemptSequence;
    stageRun.approval = stageState.approval ? { ...stageState.approval } : null;
    stageRun.failure = stageState.failure;
    stageRun.commitPoint = stageState.commitPoint;
    stageRun.workSourceState = stageState.workSourceState ?? { evaluated: false };
    restoreTasks(stageRun, stageState.tasks);
    restoreChecks(stageRun, stageState.checks);
  }
}

function restoreTasks(stageRun: ReturnType<WorkflowRun['currentStageRun']>, taskStates: TaskRunState[]): void {
  stageRun.tasks.splice(0, stageRun.tasks.length);
  for (const taskState of taskStates) {
    const task = stageRun.restoreTaskState(
      taskState.id,
      taskState.title,
      taskState.order,
      taskState.uses,
    );
    task.status = taskState.status;
    task.dependsOn = [...taskState.dependsOn];
    task.attempts = taskState.attempts;
    task.duration = taskState.duration;
    task.artifacts = [...taskState.artifacts];
    task.events = [...taskState.events];
    task.output = taskState.output;
    task.reason = taskState.reason;
    task.causedBy = taskState.causedBy;
    task.resetBy = taskState.resetBy;
    task.latestAttempt = taskState.latestAttempt;
  }
}

function restoreChecks(stageRun: ReturnType<WorkflowRun['currentStageRun']>, checkStates: CheckRunState[]): void {
  stageRun.checks.splice(0, stageRun.checks.length);
  for (const checkState of checkStates) {
    const check = stageRun.restoreCheckState(checkState.name, checkState.title);
    check.status = checkState.status;
    check.message = checkState.message;
    check.output = checkState.output;
    check.runCount = checkState.runCount;
    check.latestAttempt = checkState.latestAttempt;
  }
}
