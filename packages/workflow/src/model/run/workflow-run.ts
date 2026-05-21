import type { WorkflowDefinitionSnapshot } from '../workflow-definition-snapshot';
import type { WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import type { FailureDetails, WorkflowRunStatus, WorkflowWork } from './types';

export class WorkflowRun {
  readonly stageRuns: StageRun[];
  status: WorkflowRunStatus = 'pending';
  currentStage: WorkflowStageId;
  failure: FailureDetails | null = null;

  constructor(
    readonly id: string,
    readonly definitionSnapshot: WorkflowDefinitionSnapshot,
  ) {
    if (definitionSnapshot.stages.length === 0) {
      throw new WorkflowDomainError('WorkflowRun requires at least one stage definition');
    }
    this.stageRuns = definitionSnapshot.stages.map((definition, index) => new StageRun(definition, index));
    this.currentStage = definitionSnapshot.stages[0].stage;
  }

  get stageOrder(): WorkflowStageId[] {
    return this.definitionSnapshot.stages.map(definition => definition.stage);
  }

  start(): void {
    if (this.status !== 'pending') {
      throw new WorkflowDomainError(`WorkflowRun is ${this.status}`);
    }
    this.status = 'running';
    this.stageRuns[0].start();
  }

  next(): WorkflowWork {
    if (this.status === 'passed') return { kind: 'complete' };
    if (this.status === 'failed') {
      if (!this.failure) {
        throw new WorkflowDomainError('Failed WorkflowRun requires failure details');
      }
      return { kind: 'failed', reason: this.failure };
    }
    if (this.status !== 'running') return { kind: 'blocked', stage: this.currentStage, reason: { complete: false, reason: 'workflow-not-running', stage: this.currentStage } };

    const stageRun = this.stageRuns.find(candidate => candidate.stage === this.currentStage);
    if (!stageRun) return { kind: 'blocked', stage: this.currentStage, reason: { complete: false, reason: 'missing-current-stage', stage: this.currentStage } };
    if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval', stage: stageRun.stage };
    if (stageRun.status === 'failed') {
      if (!stageRun.failure) {
        return { kind: 'blocked', stage: stageRun.stage, reason: { complete: false, reason: 'stage-failed', stage: stageRun.stage } };
      }
      return { kind: 'failed', reason: stageRun.failure };
    }
    if (stageRun.status !== 'running') return { kind: 'blocked', stage: stageRun.stage, reason: { complete: false, reason: 'stage-not-running', stage: stageRun.stage } };

    if (stageRun.definition.tasksFrom && !stageRun.workSourceState.evaluated) {
      return { kind: 'task-source', stage: stageRun.stage };
    }

    const taskDefinition = stageRun.currentTaskDefinition;
    if (taskDefinition) return { kind: 'task', stage: stageRun.stage, taskId: taskDefinition.id };

    const check = stageRun.checks.find(candidate => candidate.status === 'pending');
    if (check) return { kind: 'check', stage: stageRun.stage, checkName: check.name };

    return { kind: 'complete' };
  }
}
