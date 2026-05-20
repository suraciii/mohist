import { Stage } from '../../types';
import {
  WorkflowRun,
  type CausedByMetadata,
  type CheckRunStatus,
  type FailureDetails,
  type FreezePoint,
  type CompiledStageDefinition,
  type StageRunSnapshot,
  type TaskResetMetadata,
  type TaskRunStatus,
  type WorkflowDefinitionSnapshot,
  type WorkflowRunSnapshot,
  type WorkSourceState,
} from './index';
import { getWorkflowUseDefinition, inferWorkflowCheckUse, inferWorkflowTaskUse } from '../uses-catalog';

function isCausedByMetadata(value: unknown): value is CausedByMetadata {
  return Boolean(value && typeof value === 'object' && 'type' in value && typeof (value as { type?: unknown }).type === 'string');
}

function isTaskResetMetadata(value: unknown): value is TaskResetMetadata {
  return Boolean(value && typeof value === 'object' && (value as { type?: unknown }).type === 'workflow-policy');
}

function extractDeliveryMetadata(output: unknown): FreezePoint['delivery'] {
  const data = unwrapTaskOutput(output);
  if (!data) return {};
  return {
    targetBranch: typeof data.targetBranch === 'string' ? data.targetBranch : undefined,
    baseSha: typeof data.baseSha === 'string' ? data.baseSha : undefined,
    candidateHeadSha: typeof data.candidateHeadSha === 'string' ? data.candidateHeadSha : undefined,
    landedSha: typeof data.landedSha === 'string' ? data.landedSha : typeof data.mergedSha === 'string' ? data.mergedSha : undefined,
    rebased: typeof data.rebased === 'boolean' ? data.rebased : undefined,
  };
}

function unwrapTaskOutput(output: unknown): Record<string, unknown> | null {
  if (!output || typeof output !== 'object') return null;
  const data = output as Record<string, unknown>;
  if (data.kind === 'service-call-task' && data.result && typeof data.result === 'object') {
    return data.result as Record<string, unknown>;
  }
  return data;
}

function inferStageFailure(stage: Stage, snapshot: StageRunSnapshot): FailureDetails | null {
  if (snapshot.failure) return snapshot.failure;
  if (snapshot.status !== 'failed') return null;

  const failedTask = snapshot.tasks.find(task => task.status === 'failed');
  if (failedTask) {
    return {
      reason: 'task-failed',
      stage,
      taskId: failedTask.id,
      message: failedTask.reason ?? undefined,
      causedBy: failedTask.causedBy ?? undefined,
    };
  }

  const failedCheck = snapshot.checks.find(check => check.status === 'failed' || check.status === 'error');
  if (failedCheck && snapshot.freezePoint) {
    return {
      reason: 'post-delivery-check-failed',
      stage,
      checkName: failedCheck.name,
      message: failedCheck.message ?? undefined,
    };
  }

  if (failedCheck) {
    return {
      reason: 'check-unrepaired',
      stage,
      checkName: failedCheck.name,
      message: failedCheck.message ?? undefined,
      causedBy: { type: 'check-failure', checkName: failedCheck.name, message: failedCheck.message ?? undefined },
    };
  }

  if (snapshot.approval?.status === 'rejected') {
    return {
      reason: 'approval-rejected',
      stage,
      message: typeof snapshot.approval.output === 'string' ? snapshot.approval.output : undefined,
    };
  }

  return null;
}

export function hydrateWorkflowRun(
  snapshot: WorkflowRunSnapshot,
): WorkflowRun {
  const workflowDefinitionSnapshot = snapshot.workflowDefinitionSnapshot;
  if (!workflowDefinitionSnapshot) {
    throw new Error('Cannot hydrate WorkflowRun without workflow definition snapshot');
  }
  const definitions = workflowDefinitionSnapshot.compiledStageDefinitions;
  const workflow = WorkflowRun.startWorkflow({
    id: snapshot.id,
    issueId: snapshot.issueId,
    issueNumber: snapshot.issueNumber,
    definitions,
    workflowDefinitionSnapshot,
  }).run;

  workflow.status = snapshot.status;
  workflow.currentStage = snapshot.currentStage;
  workflow.failure = snapshot.failure;

  for (const stageSnapshot of snapshot.stageRuns) {
    const stageRun = workflow.stageRun(stageSnapshot.stage);
    stageRun.status = stageSnapshot.status;
    stageRun.attemptSequence = stageSnapshot.attemptSequence ?? 1;
    stageRun.approval = stageSnapshot.approval ? { ...stageSnapshot.approval } : null;
    const stageDefinition = definitions.find(definition => definition.stage === stageSnapshot.stage);
    stageRun.freezePoint = freezePointFromStageSnapshot(stageSnapshot.stage, stageSnapshot, stageDefinition);
    stageRun.failure = inferStageFailure(stageSnapshot.stage, { ...stageSnapshot, freezePoint: stageRun.freezePoint });
    const legacyStageSnapshot = stageSnapshot as StageRunSnapshot & { buildWorkSourceState?: WorkSourceState };
    const workSourceState = stageSnapshot.workSourceState ?? legacyStageSnapshot.buildWorkSourceState;
    if (workSourceState) {
      stageRun.workSourceState = workSourceState;
    }

    stageRun.tasks.splice(0, stageRun.tasks.length);
    for (const taskSnapshot of [...stageSnapshot.tasks].sort((a, b) => a.order - b.order || a.id.localeCompare(b.id))) {
      const task = stageRun.materializeTaskForPersistence(taskSnapshot.id, taskSnapshot.title, taskSnapshot.order);
      task.status = taskSnapshot.status;
      task.dependsOn = [...(taskSnapshot.dependsOn ?? [])];
      task.attempts = taskSnapshot.attempts;
      task.duration = taskSnapshot.duration;
      task.artifacts = [...taskSnapshot.artifacts];
      task.events = [...taskSnapshot.events];
      task.output = taskSnapshot.output;
      task.reason = taskSnapshot.reason;
      task.causedBy = isCausedByMetadata(taskSnapshot.causedBy) ? { ...taskSnapshot.causedBy } : null;
      task.resetBy = isTaskResetMetadata(taskSnapshot.resetBy) ? { ...taskSnapshot.resetBy } : null;
      task.latestAttempt = taskSnapshot.latestAttempt ? { ...taskSnapshot.latestAttempt } : null;
      if (!task.latestAttempt) {
        task.synthesizeLatestAttempt(new Date().toISOString());
      }
    }

    stageRun.checks.splice(0, stageRun.checks.length);
    for (const checkSnapshot of stageSnapshot.checks) {
      const check = stageRun.materializeCheckForPersistence(checkSnapshot.name, checkSnapshot.title);
      check.status = checkSnapshot.status;
      check.message = checkSnapshot.message;
      check.output = checkSnapshot.output;
      check.runCount = checkSnapshot.runCount;
      check.latestAttempt = checkSnapshot.latestAttempt ? { ...checkSnapshot.latestAttempt } : null;
      if (!check.latestAttempt) {
        check.synthesizeLatestAttempt(new Date().toISOString());
      }
    }
  }

  workflow.failure = snapshot.failure ?? workflow.stageRuns.find(stageRun => stageRun.failure)?.failure ?? null;
  if (workflow.status === 'failed') {
    for (const stageRun of workflow.stageRuns) {
      if (stageRun.failure && stageRun.status !== 'failed') stageRun.status = 'failed';
    }
  }
  return workflow;
}

export function repairWorkflowRunSnapshot(
  snapshot: WorkflowRunSnapshot,
): WorkflowRunSnapshot {
  const workflowDefinitionSnapshot = snapshot.workflowDefinitionSnapshot;
  if (!workflowDefinitionSnapshot) {
    throw new Error('Cannot repair WorkflowRun without workflow definition snapshot');
  }
  const definitions: CompiledStageDefinition[] = workflowDefinitionSnapshot.compiledStageDefinitions;
  const stageSnapshots = new Map(snapshot.stageRuns.map(stageRun => [stageRun.stage, stageRun]));
  const workflowRunning = snapshot.status === 'running';

  for (const [definitionIndex, definition] of definitions.entries()) {
    let stageRun = stageSnapshots.get(definition.stage);
    if (!stageRun) {
      stageRun = {
        stage: definition.stage,
        status: definition.stage === snapshot.currentStage && snapshot.status === 'running' ? 'running' : 'pending',
        order: definitionIndex,
        attemptSequence: 1,
        tasks: [],
        checks: [],
        approval: null,
        failure: null,
        freezePoint: null,
      };
      stageSnapshots.set(definition.stage, stageRun);
    }

    const shouldRepairStaticStage = workflowRunning && (definition.stage === Stage.Plan || definition.stage === Stage.Integrate);
    if (shouldRepairStaticStage) {
      for (const [taskIndex, task] of definition.tasks.entries()) {
        if (stageRun.tasks.some(existing => existing.id === task.id)) continue;
        stageRun.tasks.push({
          id: task.id,
          title: task.title,
          status: 'pending' as TaskRunStatus,
          order: taskIndex,
          dependsOn: [...(task.dependsOn ?? [])],
          attempts: 0,
          duration: 0,
          artifacts: [],
          events: [],
          output: null,
          reason: null,
          causedBy: null,
          resetBy: null,
          latestAttempt: null,
        });
      }
    }

    if (shouldRepairStaticStage) {
      for (const check of definition.checks) {
        if (stageRun.checks.some(existing => existing.name === check.name)) continue;
        stageRun.checks.push({
          name: check.name,
          title: check.title,
          status: 'pending' as CheckRunStatus,
          message: null,
          output: null,
          runCount: 0,
          latestAttempt: null,
        });
      }
    }

    stageRun.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
  }

  return {
    ...snapshot,
    stageOrder: definitions.map(definition => definition.stage),
    workflowDefinitionSnapshot,
    stageRuns: [...stageSnapshots.values()].sort((a, b) => a.order - b.order),
  };
}

export function workflowDefinitionSnapshotFromUnknown(value: unknown): WorkflowDefinitionSnapshot | null {
  if (!value || typeof value !== 'object') return null;
  const snapshot = value as Partial<WorkflowDefinitionSnapshot>;
  if (typeof snapshot.workflowId !== 'string') return null;
  if (!snapshot.source || typeof snapshot.source !== 'object') return null;
  if (!snapshot.resolvedDefinition || typeof snapshot.resolvedDefinition !== 'object') return null;
  if (!Array.isArray(snapshot.compiledStageDefinitions)) return null;
  if (typeof snapshot.capturedAt !== 'string') return null;
  return snapshot as WorkflowDefinitionSnapshot;
}

export function freezePointFromStageSnapshot(_stage: Stage, snapshot: StageRunSnapshot, definition?: CompiledStageDefinition): FreezePoint | null {
  if (snapshot.freezePoint) return snapshot.freezePoint;
  for (const task of snapshot.tasks) {
    if (task.status !== 'completed') continue;
    const uses = workflowTaskUse(definition, task.id);
    if (getWorkflowUseDefinition(uses)?.locksCode !== true) continue;
    return {
      taskId: task.id,
      delivery: extractDeliveryMetadata(task.output),
      frozenAt: new Date().toISOString(),
    };
  }
  for (const check of snapshot.checks) {
    if (check.status !== 'passed') continue;
    const uses = workflowCheckUse(definition, check.name);
    if (getWorkflowUseDefinition(uses)?.locksCode !== true) continue;
    return {
      checkName: check.name,
      delivery: extractDeliveryMetadata(check.output),
      frozenAt: new Date().toISOString(),
    };
  }
  return null;
}

function workflowTaskUse(definition: CompiledStageDefinition | undefined, taskId: string): string {
  const taskDefinition = definition?.tasks.find(task => task.id === taskId);
  const policy = definition?.taskExecutionPolicies?.find(candidate => candidate.taskId === taskId)
    ?? definition?.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
  return taskDefinition?.uses ?? inferWorkflowTaskUse(taskId, policy?.kind);
}

function workflowCheckUse(definition: CompiledStageDefinition | undefined, checkName: string): string {
  const checkDefinition = definition?.checks.find(check => check.name === checkName);
  return checkDefinition?.uses ?? inferWorkflowCheckUse(checkName);
}
