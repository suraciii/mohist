import type { WorkflowDefinitionSnapshot } from '../workflow-definition-snapshot';
import type { WorkflowStageId } from '../workflow-definition';
import { WorkflowDomainError } from '../errors';
import { StageRun } from './stage-run';
import type { FailureDetails, WorkflowRunStatus } from './types';

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
}
