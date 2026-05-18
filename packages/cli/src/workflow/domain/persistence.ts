import { Stage } from '../../types';
import {
  DEFAULT_STAGE_DEFINITIONS,
  WorkflowRun,
  type CausedByMetadata,
  type CheckRunStatus,
  type FailureDetails,
  type FreezePoint,
  type StageDefinition,
  type StageRunSnapshot,
  type TaskRunStatus,
  type WorkflowRunSnapshot,
} from './index';

function isCausedByMetadata(value: unknown): value is CausedByMetadata {
  return Boolean(value && typeof value === 'object' && 'type' in value && typeof (value as { type?: unknown }).type === 'string');
}

function extractDeliveryMetadata(output: unknown): FreezePoint['delivery'] {
  const data = unwrapTaskOutput(output);
  if (!data) return {};
  return {
    targetBranch: typeof data.targetBranch === 'string' ? data.targetBranch : undefined,
    baseSha: typeof data.baseSha === 'string' ? data.baseSha : undefined,
    candidateHeadSha: typeof data.candidateHeadSha === 'string' ? data.candidateHeadSha : undefined,
    landedSha: typeof data.landedSha === 'string' ? data.landedSha : undefined,
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
  if (failedCheck?.name === 'health:integrate' && snapshot.freezePoint) {
    return {
      reason: 'post-merge-health-failed',
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
  definitions: StageDefinition[] = DEFAULT_STAGE_DEFINITIONS,
): WorkflowRun {
  const workflow = WorkflowRun.startWorkflow({
    id: snapshot.id,
    issueId: snapshot.issueId,
    issueNumber: snapshot.issueNumber,
    definitions,
  }).run;

  workflow.status = snapshot.status;
  workflow.currentStage = snapshot.currentStage;
  workflow.failure = snapshot.failure;

  for (const stageSnapshot of snapshot.stageRuns) {
    const stageRun = workflow.stageRun(stageSnapshot.stage);
    stageRun.status = stageSnapshot.status;
    stageRun.attemptSequence = stageSnapshot.attemptSequence ?? 1;
    stageRun.approval = stageSnapshot.approval ? { ...stageSnapshot.approval } : null;
    stageRun.failure = inferStageFailure(stageSnapshot.stage, stageSnapshot);
    stageRun.freezePoint = stageSnapshot.freezePoint ? { ...stageSnapshot.freezePoint, delivery: { ...stageSnapshot.freezePoint.delivery } } : null;
    if (stageSnapshot.buildWorkSourceState) {
      stageRun.buildWorkSourceState = stageSnapshot.buildWorkSourceState;
    }

    stageRun.tasks.splice(0, stageRun.tasks.length);
    for (const taskSnapshot of [...stageSnapshot.tasks].sort((a, b) => a.order - b.order || a.id.localeCompare(b.id))) {
      const task = stageRun.materializeTaskForPersistence(taskSnapshot.id, taskSnapshot.title, taskSnapshot.order);
      task.status = taskSnapshot.status;
      task.dependsOn = [...(taskSnapshot.dependsOn ?? [])];
      task.attempts = taskSnapshot.attempts;
      task.duration = taskSnapshot.duration;
      task.artifacts = [...taskSnapshot.artifacts];
      task.output = taskSnapshot.output;
      task.reason = taskSnapshot.reason;
      task.causedBy = isCausedByMetadata(taskSnapshot.causedBy) ? { ...taskSnapshot.causedBy } : null;
    }

    stageRun.checks.splice(0, stageRun.checks.length);
    for (const checkSnapshot of stageSnapshot.checks) {
      const check = stageRun.materializeCheckForPersistence(checkSnapshot.name, checkSnapshot.title);
      check.status = checkSnapshot.status;
      check.message = checkSnapshot.message;
      check.output = checkSnapshot.output;
      check.runCount = checkSnapshot.runCount;
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
  buildTasks: Array<{
    id: string;
    title: string;
    order?: number;
    dependsOn?: string[];
    passes?: boolean;
    attempts?: number;
    durations?: number[];
    error?: string | null;
  }> = [],
  definitions: StageDefinition[] = DEFAULT_STAGE_DEFINITIONS,
): WorkflowRunSnapshot {
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
    const shouldMaterializeBuild = workflowRunning && definition.stage === Stage.Build && buildTasks.length > 0;

    if (shouldMaterializeBuild) {
      stageRun.buildWorkSourceState = {
        evaluated: true,
        tasks: buildTasks.map(t => ({
          id: t.id,
          title: t.title,
          order: t.order ?? stageRun.tasks.length,
          dependsOn: t.dependsOn ?? [],
        })),
      };
      for (const task of buildTasks) {
        const existing = stageRun.tasks.find(candidate => candidate.id === task.id);
        if (existing) {
          existing.dependsOn = [...(task.dependsOn ?? existing.dependsOn ?? [])];
          continue;
        }
        stageRun.tasks.push({
          id: task.id,
          title: task.title,
          status: 'pending',
          order: task.order ?? stageRun.tasks.length,
          dependsOn: [...(task.dependsOn ?? [])],
          attempts: 0,
          duration: 0,
          artifacts: [],
          output: null,
          reason: null,
          causedBy: null,
        });
      }
    } else if (shouldRepairStaticStage) {
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
          output: null,
          reason: null,
          causedBy: null,
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
        });
      }
    }

    stageRun.tasks.sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
  }

  return {
    ...snapshot,
    stageOrder: definitions.map(definition => definition.stage),
    stageRuns: [...stageSnapshots.values()].sort((a, b) => a.order - b.order),
  };
}

export function freezePointFromStageSnapshot(stage: Stage, snapshot: StageRunSnapshot): FreezePoint | null {
  if (snapshot.freezePoint) return snapshot.freezePoint;
  if (stage !== Stage.Integrate) return null;
  const mergeTask = snapshot.tasks.find(task => task.id === 'integrate:merge' && task.status === 'completed');
  if (!mergeTask) return null;
  return {
    taskId: 'integrate:merge',
    delivery: extractDeliveryMetadata(mergeTask.output),
    frozenAt: new Date().toISOString(),
  };
}
